'use strict';

const DEFAULT_PROFILE_ID = 'openai-sol';

const PROFILES = [
  { id: 'openai-sol', group: 'OpenAI', label: 'GPT-5.6 Sol', fullLabel: 'OpenAI · GPT-5.6 Sol', provider: 'openai', engine: 'codex', model: 'gpt-5.6-sol', reasoning: 'xhigh' },
  { id: 'deepseek-v4', group: 'DeepSeek', label: 'V4', fullLabel: 'DeepSeek · V4', provider: 'deepseek', engine: 'claude', model: 'deepseek-v4-flash' },
  { id: 'deepseek-v4-pro', group: 'DeepSeek', label: 'V4 Pro', fullLabel: 'DeepSeek · V4 Pro', provider: 'deepseek', engine: 'claude', model: 'deepseek-v4-pro' },
  { id: 'claude-default', group: 'Claude', label: '默认', fullLabel: 'Claude · 默认', provider: 'claude', engine: 'claude', model: '' },
  { id: 'claude-fable-5', group: 'Claude', label: 'Fable 5', fullLabel: 'Claude · Fable 5', provider: 'claude', engine: 'claude', model: 'claude-fable-5', ownerOnly: true },
  { id: 'claude-opus', group: 'Claude', label: 'Opus', fullLabel: 'Claude · Opus', provider: 'claude', engine: 'claude', model: 'opus' },
  { id: 'claude-sonnet', group: 'Claude', label: 'Sonnet', fullLabel: 'Claude · Sonnet', provider: 'claude', engine: 'claude', model: 'sonnet' },
  { id: 'claude-haiku', group: 'Claude', label: 'Haiku', fullLabel: 'Claude · Haiku', provider: 'claude', engine: 'claude', model: 'haiku' },
];

const BY_ID = new Map(PROFILES.map(p => [p.id, p]));
const LEGACY_MODELS = new Map([
  ['', 'openai-sol'],
  ['claude-fable-5', 'claude-fable-5'],
  ['opus', 'claude-opus'],
  ['sonnet', 'claude-sonnet'],
  ['haiku', 'claude-haiku'],
]);

function customClaudeProfile(model) {
  const value = String(model || '').trim();
  if (!/^claude-[a-z0-9.-]+$/i.test(value)) return null;
  return {
    id: 'claude-custom:' + value.toLowerCase(),
    group: 'Claude', label: value, fullLabel: 'Claude · ' + value,
    provider: 'claude', engine: 'claude', model: value,
  };
}

function profileById(id) {
  const value = String(id || '').trim().toLowerCase();
  if (BY_ID.has(value)) return BY_ID.get(value);
  if (value.startsWith('claude-custom:')) return customClaudeProfile(value.slice('claude-custom:'.length));
  return null;
}

function profileFromLegacyModel(model) {
  const value = String(model || '').trim().toLowerCase();
  const known = LEGACY_MODELS.get(value);
  if (known) return profileById(known);
  return customClaudeProfile(value) || profileById(DEFAULT_PROFILE_ID);
}

function profileFromStored(value) {
  return profileById(value) || profileFromLegacyModel(value);
}

function profileLabel(value, full) {
  const p = profileFromStored(value);
  return full ? p.fullLabel : p.label;
}

function profilesFor(isOwner) {
  return PROFILES.filter(p => !p.ownerOnly || isOwner);
}

function getUserProfileId(cfg, openId, isOwner) {
  const c = cfg || {};
  let stored;
  if (isOwner) stored = c.feishuChatProfile;
  else stored = c.feishuUserProfiles && c.feishuUserProfiles[openId];
  const direct = profileById(stored);
  if (direct && (!direct.ownerOnly || isOwner)) return direct.id;

  const legacy = isOwner ? c.feishuChatModel : c.feishuUserModels && c.feishuUserModels[openId];
  const migrated = profileFromLegacyModel(legacy);
  if (migrated.ownerOnly && !isOwner) return DEFAULT_PROFILE_ID;
  return migrated.id;
}

function setUserProfile(cfg, openId, isOwner, profileId) {
  const p = profileById(profileId);
  if (!p || (p.ownerOnly && !isOwner)) return false;
  if (isOwner) {
    cfg.feishuChatProfile = p.id;
    cfg.feishuChatModel = p.provider === 'claude' ? p.model : '';
  } else {
    if (!cfg.feishuUserProfiles || typeof cfg.feishuUserProfiles !== 'object') cfg.feishuUserProfiles = {};
    if (!cfg.feishuUserModels || typeof cfg.feishuUserModels !== 'object') cfg.feishuUserModels = {};
    cfg.feishuUserProfiles[openId] = p.id;
    cfg.feishuUserModels[openId] = p.provider === 'claude' ? p.model : '';
  }
  return true;
}

function parseProfileInput(input, isOwner) {
  const raw = String(input || '').trim();
  const compact = raw.toLowerCase().replace(/[\s_·]+/g, '').replace(/[()（）]/g, '');
  const aliases = {
    '默认': DEFAULT_PROFILE_ID, default: DEFAULT_PROFILE_ID,
    openai: 'openai-sol', chatgpt: 'openai-sol', sol: 'openai-sol', gpt56: 'openai-sol', 'gpt-5.6': 'openai-sol', 'gpt-5.6-sol': 'openai-sol',
    deepseek: 'deepseek-v4', v4: 'deepseek-v4', flash: 'deepseek-v4', 'deepseek-v4': 'deepseek-v4', 'deepseek-v4-flash': 'deepseek-v4',
    v4pro: 'deepseek-v4-pro', pro: 'deepseek-v4-pro', 'deepseek-v4-pro': 'deepseek-v4-pro',
    claude: 'claude-default', 'claude默认': 'claude-default',
    fable: 'claude-fable-5', fable5: 'claude-fable-5', 'claude-fable-5': 'claude-fable-5',
    opus: 'claude-opus', sonnet: 'claude-sonnet', haiku: 'claude-haiku',
  };
  const id = aliases[compact] || aliases[raw.toLowerCase()];
  if (id) {
    const p = profileById(id);
    return p && (!p.ownerOnly || isOwner) ? p : null;
  }
  const custom = customClaudeProfile(raw);
  return custom && (!custom.ownerOnly || isOwner) ? custom : null;
}

function fallbackProfiles(cfg, primaryId) {
  const configured = Array.isArray(cfg && cfg.aiFallbackProfiles) ? cfg.aiFallbackProfiles : ['deepseek-v4', 'openai-sol'];
  const out = [];
  for (const id of configured) {
    const p = profileById(id);
    if (p && p.id !== primaryId && !out.some(x => x.id === p.id)) out.push(p);
  }
  if (primaryId === 'deepseek-v4-pro' && !out.some(x => x.id === 'deepseek-v4')) out.unshift(profileById('deepseek-v4'));
  return out;
}

module.exports = {
  DEFAULT_PROFILE_ID, PROFILES, profileById, profileFromStored, profileFromLegacyModel,
  profileLabel, profilesFor, getUserProfileId, setUserProfile, parseProfileInput,
  fallbackProfiles,
};
