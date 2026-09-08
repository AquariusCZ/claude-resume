using System.Diagnostics;
using Xunit;

namespace AiResume.Tests;

public sealed class GuiProviderBehaviorTests
{
    [Fact]
    public void 供应商切换中的延迟响应与刷新失败不会串用旧窗口()
    {
        DirectoryInfo? repo = new(AppContext.BaseDirectory);
        while (repo is not null && !File.Exists(Path.Combine(repo.FullName, "CLAUDE.md"))) repo = repo.Parent;
        Assert.NotNull(repo);
        string html = Path.Combine(repo.FullName, "csharp", "src", "AiResume.Gui", "wwwroot", "index.html");
        string script = Path.Combine(TestTemp.NewDir("provider-ui"), "verify.cjs");
        File.WriteAllText(script, """
            const fs = require('node:fs'), vm = require('node:vm'), assert = require('node:assert/strict');
            const source = fs.readFileSync(process.argv[2], 'utf8');
            const cut = (a,b) => source.slice(source.indexOf(a), source.indexOf(b, source.indexOf(a)));
            const elements = new Map();
            const $ = id => {
              if (!elements.has(id)) elements.set(id, {value:'', textContent:'', innerHTML:'',
                classList:{toggle(){}, remove(){}}, querySelector(){return $('nested-'+id)}, querySelectorAll(){return []}});
              return elements.get(id);
            };
            const painted = new Map(), cards = new Map();
            let arm = null, providerCatalog = [], lastQuotaState = null, lastQuotaDetail = '', scopedRows = [];
            let resolveQuota, failed = false;
            const call = method => method === 'quota.get' ? new Promise(r => resolveQuota = r)
              : Promise.reject(new Error('fixture failure'));
            const paint = (id, data) => {painted.set(id, data); $(id+'big').textContent = data.used ?? data.placeholder;};
            const esc = String, syncArm = () => {}, syncModelCatalog = () => paintProviderCards();
            const fmtNum = String;
            eval(cut('const claudeCards =', '// force 绕过额度缓存') +
                 cut('function renderProviders(', 'function syncModelCatalog(') +
                 cut('function probeProviders(', 'let lastQuotaState =') + `
              (async () => {
                providerCatalog = [{id:'deepseek',name:'DeepSeek',models:[],balance:9,currency:'CNY',quotaKnown:true,source:'fixture'},
                  {id:'claude-subscription',name:'Claude',models:[],windows:[],quotaKnown:true,source:'fixture'}];
                $('resumeProvider').value = 'deepseek';
                paintProviderCards();
                const waiting = loadServer(false);
                resolveQuota({windows:[{name:'five_hour',status:'allowed',usedPercent:12},
                  {name:'seven_day',status:'allowed',usedPercent:34}],globalHasCurrentData:true});
                await waiting;
                assert.equal($('q5big').textContent, '9 CNY', '延迟的 Claude 响应不得覆盖 DeepSeek');
                $('resumeProvider').value = 'claude-subscription';
                paintProviderCards();
                assert.equal(painted.get('q5').used,12);
                assert.equal(painted.get('q7').used,34);
                providerCatalog = [{id:'codex-active',name:'Codex',models:[],windows:[
                  {name:'primary',status:'blocked',usedPercent:12},
                  {name:'secondary',status:'allowed',usedPercent:20}],quotaKnown:true,limited:true,source:'fixture'}];
                $('resumeProvider').value = 'codex-active';
                renderProviders();
                assert.equal(painted.get('q5').blocked,true,'明确 blocked 即使小于 100% 也必须保留');
                assert.equal(painted.get('q7').blocked,false,'一个窗口限额不能把其它窗口画满');
                assert.ok($('providers').innerHTML.includes('已限额'));
                await probeProviders(false);
                assert.equal(providerCatalog[0].windows.length,0);
                assert.equal($('q5big').textContent,'额度未核实');
                assert.ok(!$('providers').innerHTML.includes('led ok'));
              })().catch(e => {console.error(e); process.exitCode=1;});
            `);
            """);
        var start = new ProcessStartInfo("node") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        start.ArgumentList.Add(script);
        start.ArgumentList.Add(html);
        using Process process = Process.Start(start)!;
        Assert.True(process.WaitForExit(10_000), "隔离前端验证超时");
        Assert.True(process.ExitCode == 0, process.StandardError.ReadToEnd());
    }
}
