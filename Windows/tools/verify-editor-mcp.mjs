// Exercise the shipped stdio server with UTF-8 messages, without a UI automation driver.
import { spawn } from 'node:child_process';
import { createInterface } from 'node:readline';
import assert from 'node:assert/strict';

const executable = process.argv[2];
assert(executable, 'Usage: node verify-editor-mcp.mjs <Compositor.Mcp.exe> [appPid] [object-selection.comp]');
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
    const timer = setTimeout(() => { pending.delete(id); reject(new Error(`Timed out: ${method}`)); }, 30000);
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
  const selectionSchema = listed.result.tools.find(tool => tool.name === 'editor_select').inputSchema.properties;
  assert(selectionSchema.mode.enum.includes('object'), 'Missing local object selection');
  for (const field of ['foregroundPoints', 'backgroundPoints', 'box', 'softEdges'])
    assert(selectionSchema[field], `Missing object prompt field: ${field}`);
  for (const name of ['editor_state', 'editor_timeline', 'editor_preview', 'editor_select', 'editor_ai_prepare', 'editor_ai_import', 'editor_ai_start', 'editor_ai_accept', 'editor_ai_cancel', 'editor_history', 'editor_save', 'editor_open', 'editor_export', 'editor_mask_color', 'editor_trim_layer'])
    assert(names.includes(name), `Missing live editor tool: ${name}`);
  for (const name of ['editor_layer_select', 'editor_layer_import', 'editor_layer_import_svg', 'editor_layer_transform', 'editor_layer_reorder', 'editor_layer_duplicate', 'editor_layer_delete', 'editor_layer_rename', 'editor_layer_visibility', 'editor_layer_opacity', 'editor_layer_blend'])
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
    if (process.argv[4]) {
      const pid = Number(process.argv[3]);
      const read = async () => {
        const response = await send('tools/call', { name: 'editor_state', arguments: { appPid: pid } });
        assert(!response.error && !response.result?.isError, JSON.stringify(response));
        return JSON.parse(response.result.content.find(item => item.type === 'text').text);
      };
      const mutate = async (name, args) => {
        const source = await read();
        const response = await send('tools/call', { name, arguments: { appPid: pid, documentToken: source.documentToken, revision: source.revision, ...args } });
        assert(!response.error && !response.result?.isError, JSON.stringify(response));
        return JSON.parse(response.result.content.find(item => item.type === 'text').text);
      };
      const opened = await mutate('editor_open', { path: process.argv[4] });
      assert(opened.objectSelection.installed, 'Install the local model before the object-selection test');
      assert.equal(opened.selection, null);
      const raster = async () => {
        const response = await send('tools/call', { name: 'editor_preview', arguments: { appPid: pid, maxSide: 512 } });
        assert(!response.error && !response.result?.isError, JSON.stringify(response));
        return response.result.content.find(item => item.type === 'image').data;
      };
      const original = await raster();
      const selected = await mutate('editor_select', { mode: 'object',
        foregroundPoints: [[opened.width * .5, opened.height * .5]],
        backgroundPoints: [[opened.width * .95, opened.height * .95]], softEdges: false });
      assert(selected.selection?.width > 0 && selected.selection?.height > 0, 'No object was selected');
      assert.equal(await raster(), original, 'Object selection modified image pixels');
      const undone = await mutate('editor_history', { action: 'undo' });
      assert.equal(undone.selection, null); assert(undone.canRedo);
      const redone = await mutate('editor_history', { action: 'redo' });
      assert.deepEqual(redone.selection, selected.selection, 'Redo changed object bounds');
      const boxed = await mutate('editor_select', { mode: 'object', box: { x: opened.width * .15, y: opened.height * .2,
        width: opened.width * .7, height: opened.height * .65 } });
      assert(boxed.selection?.width > 0, 'Box-only object selection failed');
      console.log(JSON.stringify({ status: 'passed', toolCount: names.length, appPid: pid,
        objectSelection: 'point/exclude, box, unchanged image, Undo/Redo', backend: boxed.objectSelection.backend,
        pointBounds: selected.selection, boxBounds: boxed.selection }));
    } else console.log(JSON.stringify({ status: 'passed', toolCount: names.length, state, previewImage: true }));
  } else console.log(JSON.stringify({ status: 'passed', toolCount: names.length }));
} finally {
  server.stdin.end();
  await new Promise(resolve => { if (server.exitCode !== null) resolve(); else { server.once('exit', resolve); setTimeout(() => { server.kill(); resolve(); }, 1000).unref(); } });
}
