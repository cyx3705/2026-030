// Read-only comparison of the same session through dispatch and admission readers.
const root = '/var/lib/openclaw-wsl/state/tools/node-v24.22.1/lib/node_modules/openclaw/dist/';
const { i: readDispatch } = await import(root + 'session-entry-read-runtime-B8-EZTzc.mjs');
const { y: readAdmission } = await import(root + 'session-accessor.sqlite-entry-DvEiQ1z_.mjs');
for (const storePath of [
  '/mnt/f/OpenClaw/state/agents/main/agent/openclaw-agent.sqlite',
  '/var/lib/openclaw-agents/agent/openclaw-agent.sqlite',
  '/mnt/f/OpenClaw/state/agents/main/sessions/sessions.json',
]) {
  const input = { agentId: 'main', sessionKey: 'agent:main:main', storePath, readConsistency: 'latest', clone: false };
  const dispatch = await readDispatch(input);
  const admission = await readAdmission(input);
  console.log(JSON.stringify({storePath, dispatch: dispatch?.sessionId, admission: admission.entry?.sessionId, equal: dispatch?.sessionId === admission.entry?.sessionId}));
  await admission.databaseClaim.release();
}
process.exit(0);
