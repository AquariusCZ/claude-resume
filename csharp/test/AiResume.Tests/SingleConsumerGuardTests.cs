using System;
using System.Collections.Generic;
using System.Linq;
using AiResume.Wrapper;
using Xunit;

namespace AiResume.Tests
{
    public class SingleConsumerGuardTests
    {
        private sealed class FakeProcessLister : IRunningProcessLister
        {
            private readonly IReadOnlyList<RunningProcessInfo>? _processes;
            private readonly Exception? _exception;
            public int ListCallCount { get; private set; }

            public FakeProcessLister(IReadOnlyList<RunningProcessInfo>? processes = null, Exception? exception = null)
            {
                _processes = processes;
                _exception = exception;
            }

            /// <summary>
            /// 假枚举器默认声明「能读命令行」:测试的关注点是冲突识别逻辑,
            /// 而看不到命令行会让守卫在第一步就短路成 Unverifiable,后面的用例全测不到。
            /// 单独用一个用例覆盖 ProvidesCommandLine 为 false 的分支。
            /// </summary>
            public bool ProvidesCommandLine { get; init; } = true;

            public IReadOnlyList<RunningProcessInfo> List()
            {
                ListCallCount++;
                if (_exception != null)
                {
                    throw _exception;
                }
                return _processes!;
            }
        }

        private static SingleConsumerGuard CreateGuard(FakeProcessLister lister, int selfPid = 12345)
        {
            return new SingleConsumerGuard(lister, selfPid);
        }

        [Fact]
        public void FeishuNotConfigured_ReturnsClear_AndDoesNotCallLister()
        {
            var lister = new FakeProcessLister(new List<RunningProcessInfo>());
            var guard = CreateGuard(lister);

            var result = guard.Check(false);

            Assert.Equal(ConsumerGuardVerdict.Clear, result.Verdict);
            Assert.Empty(result.Conflicts);
            Assert.True(result.CanStart);
            Assert.Equal(0, lister.ListCallCount);
        }

        [Fact]
        public void NoRelevantProcesses_ReturnsClear()
        {
            var lister = new FakeProcessLister(new List<RunningProcessInfo>
            {
                new RunningProcessInfo(100, "notepad", null),
                new RunningProcessInfo(200, "explorer", "C:\\Windows\\explorer.exe")
            });
            var guard = CreateGuard(lister);

            var result = guard.Check(true);

            Assert.Equal(ConsumerGuardVerdict.Clear, result.Verdict);
            Assert.Empty(result.Conflicts);
            Assert.True(result.CanStart);
        }

        [Fact]
        public void ListerThrows_ReturnsUnverifiable_FailClosed()
        {
            // 必须 fail-closed:核验不了不等于没有冲突,此时启动会导致两个消费者抢同一个飞书应用的事件。
            var lister = new FakeProcessLister(exception: new InvalidOperationException("模拟列表失败"));
            var guard = CreateGuard(lister);

            var result = guard.Check(true);

            Assert.Equal(ConsumerGuardVerdict.Unverifiable, result.Verdict);
            Assert.False(result.CanStart);
            Assert.False(string.IsNullOrEmpty(result.Reason));
        }

        [Fact]
        public void ListerReturnsNull_ReturnsUnverifiable()
        {
            var lister = new FakeProcessLister(processes: null);
            var guard = CreateGuard(lister);

            var result = guard.Check(true);

            Assert.Equal(ConsumerGuardVerdict.Unverifiable, result.Verdict);
            Assert.False(result.CanStart);
            Assert.False(string.IsNullOrEmpty(result.Reason));
        }

        [Fact]
        public void CommandLineContainsFeishuAgent_ReturnsConflict_LegacyNodeAgent()
        {
            var lister = new FakeProcessLister(new List<RunningProcessInfo>
            {
                new RunningProcessInfo(100, "node", "node C:\\app\\FeiShu-Agent.JS --config=prod")
            });
            var guard = CreateGuard(lister);

            var result = guard.Check(true);

            Assert.Equal(ConsumerGuardVerdict.Conflict, result.Verdict);
            Assert.Single(result.Conflicts);
            Assert.Equal("legacy-node-agent", result.Conflicts[0].Kind);
            Assert.Equal(100, result.Conflicts[0].Pid);
        }

        [Theory]
        [InlineData("cc-connect")]
        [InlineData("cc-connect.exe")]
        [InlineData("CC-Connect.EXE")]
        public void ProcessNameIsCcConnect_NotSelf_ReturnsConflict(string processName)
        {
            var lister = new FakeProcessLister(new List<RunningProcessInfo>
            {
                new RunningProcessInfo(999, processName, null)
            });
            var guard = CreateGuard(lister, selfPid: 12345);

            var result = guard.Check(true);

            Assert.Equal(ConsumerGuardVerdict.Conflict, result.Verdict);
            Assert.Single(result.Conflicts);
            Assert.Equal("cc-connect", result.Conflicts[0].Kind);
            Assert.Equal(999, result.Conflicts[0].Pid);
        }

        [Fact]
        public void ProcessNameIsCcConnect_ButSelfPid_ReturnsClear()
        {
            var lister = new FakeProcessLister(new List<RunningProcessInfo>
            {
                new RunningProcessInfo(12345, "cc-connect", null)
            });
            var guard = CreateGuard(lister, selfPid: 12345);

            var result = guard.Check(true);

            Assert.Equal(ConsumerGuardVerdict.Clear, result.Verdict);
            Assert.Empty(result.Conflicts);
            Assert.True(result.CanStart);
        }

        [Fact]
        public void MultipleConflicts_AreSortedByPidAscending()
        {
            var lister = new FakeProcessLister(new List<RunningProcessInfo>
            {
                new RunningProcessInfo(300, "cc-connect", null),
                new RunningProcessInfo(100, "node", "node feishu-agent.js"),
                new RunningProcessInfo(200, "cc-connect.exe", null)
            });
            var guard = CreateGuard(lister);

            var result = guard.Check(true);

            Assert.Equal(ConsumerGuardVerdict.Conflict, result.Verdict);
            Assert.Equal(3, result.Conflicts.Count);
            Assert.Equal(new[] { 100, 200, 300 }, result.Conflicts.Select(c => c.Pid).ToArray());
        }

        [Fact]
        public void SameProcessMatchesBoth_OnlyCountedOnce_KindIsLegacyNodeAgent()
        {
            var lister = new FakeProcessLister(new List<RunningProcessInfo>
            {
                new RunningProcessInfo(777, "cc-connect", "node feishu-agent.js --app_secret=SUPERSECRETVALUE")
            });
            var guard = CreateGuard(lister);

            var result = guard.Check(true);

            Assert.Equal(ConsumerGuardVerdict.Conflict, result.Verdict);
            Assert.Single(result.Conflicts);
            Assert.Equal("legacy-node-agent", result.Conflicts[0].Kind);
            Assert.Equal(777, result.Conflicts[0].Pid);
        }

        [Fact]
        public void CommandLineContainsAppSecret_DetailMustNotLeakSecret()
        {
            // 安全红线:命令行可能带飞书 app_secret,一旦进入结果就会流进日志与界面。
            var lister = new FakeProcessLister(new List<RunningProcessInfo>
            {
                new RunningProcessInfo(111, "node", "node feishu-agent.js --app_secret=SUPERSECRETVALUE"),
                new RunningProcessInfo(222, "cc-connect", "C:\\tools\\cc-connect.exe --app_secret=SUPERSECRETVALUE")
            });
            var guard = CreateGuard(lister);

            var result = guard.Check(true);

            Assert.Equal(ConsumerGuardVerdict.Conflict, result.Verdict);
            Assert.NotEmpty(result.Conflicts);
            foreach (var conflict in result.Conflicts)
            {
                Assert.DoesNotContain("SUPERSECRETVALUE", conflict.Detail);
            }
        }

        [Fact]
        public void Detail_DoesNotContainPathSeparators()
        {
            var lister = new FakeProcessLister(new List<RunningProcessInfo>
            {
                new RunningProcessInfo(555, @"C:\tools\cc-connect.exe", null)
            });
            var guard = CreateGuard(lister);

            var result = guard.Check(true);

            Assert.Equal(ConsumerGuardVerdict.Conflict, result.Verdict);
            Assert.Single(result.Conflicts);
            Assert.DoesNotContain("\\", result.Conflicts[0].Detail);
        }

        [Fact]
        public void NullCommandLine_DoesNotThrow()
        {
            var lister = new FakeProcessLister(new List<RunningProcessInfo>
            {
                new RunningProcessInfo(444, "cc-connect", null)
            });
            var guard = CreateGuard(lister);

            var result = guard.Check(true);

            Assert.Equal(ConsumerGuardVerdict.Conflict, result.Verdict);
            Assert.Single(result.Conflicts);
        }

        [Fact]
        public void 可读Vbs守护命令行也必须判为旧消费者冲突()
        {
            var lister = new FakeProcessLister(new List<RunningProcessInfo>
            {
                new RunningProcessInfo(101, "wscript.exe", "wscript.exe C:\\ClaudeResume\\feishu-launch.vbs")
            });

            ConsumerGuardResult result = CreateGuard(lister).Check(true);

            Assert.Equal(ConsumerGuardVerdict.Conflict, result.Verdict);
            ConflictingProcess conflict = Assert.Single(result.Conflicts);
            Assert.Equal(101, conflict.Pid);
            Assert.Equal("legacy-node-agent", conflict.Kind);
            Assert.Contains("feishu-launch.vbs", conflict.Detail, StringComparison.OrdinalIgnoreCase);
        }

        [Theory]
        [InlineData("node")]
        [InlineData("wscript.exe")]
        [InlineData("cscript")]
        [InlineData("cmd.exe")]
        public void 脚本宿主命令行不可读时必须failClosed(string processName)
        {
            var lister = new FakeProcessLister(new List<RunningProcessInfo>
            {
                new RunningProcessInfo(444, processName, null)
            });

            var result = CreateGuard(lister).Check(true);

            Assert.Equal(ConsumerGuardVerdict.Unverifiable, result.Verdict);
            Assert.False(result.CanStart);
            Assert.Empty(result.Conflicts);
        }
    
        /// <summary>
        /// 安全红线:枚举器读不到命令行时,无法排除现役 node agent 仍在消费同一飞书应用,
        /// 必须判成无法核验而不是「没找到冲突」——后者等于凭空担保。
        /// </summary>
        [Fact]
        public void 枚举器读不到命令行_判无法核验而非放行()
        {
            var lister = new FakeProcessLister(new List<RunningProcessInfo>()) { ProvidesCommandLine = false };
            var guard = CreateGuard(lister);

            var result = guard.Check(true);

            Assert.Equal(ConsumerGuardVerdict.Unverifiable, result.Verdict);
            Assert.False(result.CanStart);
            Assert.NotNull(result.Reason);
            Assert.Equal(0, lister.ListCallCount);
        }

        /// <summary>默认枚举器如实声明看不到命令行(不得谎报能力)。</summary>
        [Fact]
        public void 默认枚举器如实声明看不到命令行()
        {
            Assert.False(new DiagnosticsRunningProcessLister().ProvidesCommandLine);
        }
    }
}
