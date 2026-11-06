// Exercise the shipped stdio server with UTF-8 messages, without a UI automation driver.
import { spawn } from 'node:child_process';
import { createInterface } from 'node:readline';
import assert from 'node:assert/strict';

const executable = process.argv[2];
assert(executable, 'Usage: node verify-editor-mcp.mjs <Compositor.Mcp.exe> [appPid]');
const server = spawn(executable, [], { windowsHide: true, stdio: ['pipe', 'pipe', 'pipe'] });
const pending = new Map();
let sequence = 0;
let diagnostics = '';
server.stderr.on('data', data => { diagnostics += data; });
createInterface({ input: server.stdout }).on('line', line => {
  const response = JSON.parse(line);
  const receiver = pending.get(response.id);
  if (receiver) { pending.delete(response.id); receiver(response); }
});
async function send(method, params) {
  const id = ++sequence;
  const response = new Promise((resolve, reject) => {
    const timer = setTimeout(() => { pending.delete(id); reject(new Error(`Timed out: ${method}`)); }, 15000);
    pending.set(id, value => { clearTimeout(timer); resolve(value); });
  });
  server.stdin.write(JSON.stringify({ jsonrpc: '2.0', id, method, params }) + '\n');
  return await response;
}
try {
  const initialized = await send('initialize', { protocolVersion: '2025-06-18', capabilities: {}, clientInfo: { name: 'compositor-acceptance', version: '1' } });
  assert(!initialized.error, JSON.stringify(initialized.error));
  server.stdin.write(JSON.stringify({ jsonrpc: '2.0', method: 'notifications/initialized' }) + '\n');
  const listed = await send('tools/list', {});
  const names = listed.result.tools.map(tool => tool.name);
  assert(!names.includes('editor_ai_tasks') && !names.includes('editor_ai_resume'), 'Retired provider tools remain advertised');
  const aiSchema = listed.result.tools.find(tool => tool.name === 'editor_ai_start').inputSchema.properties;
  assert.deepEqual(aiSchema.connection.enum, ['api', 'account'], 'Unexpected built-in provider');
  for (const name of ['editor_state', 'editor_timeline', 'editor_preview', 'editor_select', 'editor_ai_start', 'editor_ai_accept', 'editor_ai_cancel', 'editor_history', 'editor_save', 'editor_open', 'editor_export', 'editor_mask_color', 'editor_trim_layer'])
    assert(names.includes(name), `Missing live editor tool: ${name}`);
  for (const name of ['editor_layer_select', 'editor_layer_import', 'editor_layer_transform', 'editor_layer_reorder', 'editor_layer_duplicate', 'editor_layer_delete', 'editor_layer_rename', 'editor_layer_visibility', 'editor_layer_opacity', 'editor_layer_blend'])
    assert(names.includes(name), `Missing live layer tool: ${name}`);
  for (const name of ['editor_batch', 'editor_text', 'editor_adjustment', 'editor_mask', 'editor_layout'])
    assert(names.includes(name), `Missing editor workflow tool: ${name}`);
  if (process.argv[3]) {
    const response = await send('tools/call', { name: 'editor_state', arguments: { appPid: Number(process.argv[3]) } });
    assert(!response.error && !response.result?.isError, JSON.stringify(response));
    const state = JSON.parse(response.result.content.find(item => item.type === 'text').text);
    assert(state.documentToken && Number.isInteger(state.revision), 'State lacks an editing identity');
    const preview = await send('tools/call', { name: 'editor_preview', arguments: { appPid: Number(process.argv[3]), maxSide: 256 } });
    assert(preview.result?.content.some(item => item.type === 'image' && item.mimeType === 'image/png'), 'Preview was not an MCP image');
    console.log(JSON.stringify({ status: 'passed', toolCount: names.length, state, previewImage: true }));
  } else console.log(JSON.stringify({ status: 'passed', toolCount: names.length }));
} finally {
  server.stdin.end();
  await new Promise(resolve => { if (server.exitCode !== null) resolve(); else { server.once('exit', resolve); setTimeout(() => { server.kill(); resolve(); }, 1000).unref(); } });
}
