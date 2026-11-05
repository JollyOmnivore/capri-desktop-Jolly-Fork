import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { createServer } from 'node:http';
import { createInterface } from 'node:readline';
import { mkdtempSync, mkdirSync, rmSync, writeFileSync, symlinkSync, readFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
const home = mkdtempSync(join(tmpdir(), 'capri-computer-runtime-'));
const workspace = join(home, 'workspace'); mkdirSync(workspace);
const requests = [];
let step = 0, approvals = 0, visuals = 0;
const completedTools = [];
let lastReply = "";
const localModel = process.argv.includes('--local-model');
const fixture = process.argv.includes('--fixture') || localModel;
let catalog;
if (localModel) {
    const { CuaDriver } = await import('../../out/dsh-runtime/node_modules/@trycua/cua-driver/dist/index.js');
    const driver = CuaDriver.create();
    try { catalog = await driver.listToolsJson(); } finally { await driver.shutdown(); driver.uniffiDestroy(); }
}
let runtime = resolve('out/dsh-runtime');
const target = { pid: 8124, window_id: 42 };
const plan = [ ['computer_list_windows', {}], ['computer_get_window_state', target], ['computer_click', { ...target, element_token: 'fixture' }], ['computer_get_window_state', target] ];
if (fixture) {
    runtime = join(home, 'runtime');
    const driver = join(runtime, 'node_modules', '@trycua', 'cua-driver');
    mkdirSync(join(driver, 'dist'), { recursive: true });
    symlinkSync(resolve('out/dsh-runtime/node_modules/@deepseek-ai'), join(runtime, 'node_modules', '@deepseek-ai'));
    writeFileSync(join(runtime, 'package.json'), '{"private":true}');
    writeFileSync(join(driver, 'package.json'), '{"version":"0.28.0","type":"module"}');
    writeFileSync(join(driver, 'dist', 'index.js'), `
    const tools = ${JSON.stringify(['list_apps', 'list_windows', 'get_window_state', 'click', 'type_text', 'press_key', 'scroll'])};
    let value = "";
    export class CuaDriver {
        static create() { return new CuaDriver(); }
        async listToolsJson() { if (${JSON.stringify(catalog ?? null)}) return ${JSON.stringify(catalog ?? null)}; return JSON.stringify({ tools: tools.map(name => ({ name, inputSchema: { type: 'object', properties: { pid: {type:'integer'}, window_id: {type:'integer'}, element_token: {type:'string'} } } })) }); }
        async callTool(name, raw) {
            if (name === "type_text") value = JSON.parse(raw).text;
            const bounds = { x:100, y:100, width:160, height:80 };
            const structuredContent = name === 'check_permissions' ? { accessibility:true,screen_recording:true } : name === 'list_windows' ? { windows: [{pid:8124,window_id:42,app_name:'Disposable fixture',title:'Computer test',is_on_screen:true,on_current_space:true,bounds}] } : name === 'get_window_state' ? {pid:8124,window_id:42,window_bounds:bounds,snapshot_id:'s00000001',elements:[{element_index:1,element_token:'fixture',role:'AXTextArea',label:'Document text',value,frame:{x:120,y:120,w:40,h:20}}]} : {effect:'delivered'};
            const content = [{type:'text',text:JSON.stringify(structuredContent)}];
            if (name === 'get_window_state') content.push({type:'image',mimeType:'image/png',data:${JSON.stringify(readFileSync('assets/cursor.png').toString('base64'))}});
            return {rawJson:JSON.stringify({structuredContent,content})};
        }
        async shutdown() {}
        uniffiDestroy() {}
    }`);
}
const server = createServer(async (req, res) => {
    let raw = ''; for await (const part of req) raw += part;
    if (localModel) {
        if (req.url === '/api/show') { res.setHeader('Content-Type','application/json'); res.end(JSON.stringify({capabilities:['completion','tools']})); return; }
        const response = await fetch('http://127.0.0.1:11434' + req.url, { method: 'POST', headers: {'Content-Type':'application/json'}, body:raw });
        const body = await response.text();
        if (req.url !== '/api/show') {
            requests.push(JSON.parse(raw));
            for (const line of body.split('\n')) {
                if (!line.startsWith('data: {')) continue;
                const part = JSON.parse(line.slice(6));
                for (const choice of part.choices ?? []) { if (choice.finish_reason) console.log('MODEL finish:', choice.finish_reason); for (const tool of choice.delta?.tool_calls ?? []) if (tool.function?.name) console.log('MODEL tool:', tool.function.name); }
            }
        }
        res.statusCode = response.status; res.setHeader('Content-Type', response.headers.get('content-type')); res.end(body); return;
    }
    if (req.url === '/api/show') { res.setHeader('Content-Type', 'application/json'); res.end(JSON.stringify({ capabilities: ['completion', 'tools', 'vision'] })); return; }
    requests.push(JSON.parse(raw));
    const planned = fixture && step < plan.length ? plan[step] : !fixture && step === 0 ? ['computer_list_windows', {}] : null;
    step++;
    const delta = planned ? { role: 'assistant', tool_calls: [{ index: 0, id: 'computer-fixture', type: 'function', function: { name: planned[0], arguments: JSON.stringify(planned[1]) } }] } : { role: 'assistant', content: fixture ? 'Observed the fixture after clicking.' : 'Permission was declined; no desktop was read.' };
    const frame = (delta, finish_reason) => 'data: ' + JSON.stringify({ id: 'test', object: 'chat.completion.chunk', created: 1, model: 'qwen3.5:9b', choices: [{ index: 0, delta, finish_reason }] }) + '\n\n';
    res.setHeader('Content-Type', 'text/event-stream'); res.end(frame(delta, null) + frame({}, planned ? 'tool_calls' : 'stop') + 'data: [DONE]\n\n');
});
await new Promise(r => server.listen(0, '127.0.0.1', r));
const child = spawn(process.execPath, [resolve('integrations/dsh/bridge.mjs'), runtime], { cwd: workspace, env: { HOME: process.env.HOME, PATH: process.env.PATH, TMPDIR: tmpdir(), DSH_HOME: join(home, 'dsh'), CAPRI_LOCAL_MODEL_KEY: 'ollama', DSH_TELEMETRY_DISABLED: '1', DO_NOT_TRACK: '1' }, stdio: ['pipe', 'pipe', 'pipe'] });
let errors = ''; child.stderr.on('data', x => errors = (errors + x).slice(-4000));
let id = 0; const waiting = new Map();
function send(message) { child.stdin.write(JSON.stringify({ jsonrpc: '2.0', ...message }) + '\n'); }
function request(method, params) { const key = ++id; return new Promise((resolve, reject) => { waiting.set(key, { resolve, reject }); send({ id: key, method, params }); }); }
createInterface({ input: child.stdout }).on('line', line => {
    const frame = JSON.parse(line);
    if (frame.method === 'capri/approval') { approvals++; assert(frame.params.tool.startsWith('computer_')); send({ id: frame.id, result: { allowed: fixture } }); }
    else if (frame.method === 'capri/computer') { assert(fixture, 'Denied desktop observation reached input admission'); visuals++; send({ id: frame.id, result: {ready:true} }); }
    else if (localModel && frame.method === 'session.event') { const event=frame.params.event; if (event.type === 'turn/end') console.log('TURN END',JSON.stringify(event.data)); if (event.type === 'tool/result') completedTools.push(event.data.message); if (event.type === 'assistant/message') { lastReply = event.data.message.content.filter(b=>b.type==='text').map(b=>b.text).join(''); console.log('ASSISTANT',JSON.stringify(event.data.message.content.filter(b=>b.type==='text'||b.type==='tool-call')).slice(0,1000)); } }
    else if (frame.id != null && waiting.has(frame.id)) { const w = waiting.get(frame.id); waiting.delete(frame.id); frame.error ? w.reject(new Error(JSON.stringify(frame.error))) : w.resolve(frame.result); }
});
child.on('exit', code => { for (const w of waiting.values()) w.reject(new Error('Bridge exited ' + code + ': ' + errors)); });
const timeout = setTimeout(() => child.kill('SIGTERM'), 45000);
try {
    await request('capri/initialize', { cwd: workspace, port: server.address().port, model: 'qwen3.5:9b', persona: 'Test fixture only.', computer: true, hostPid: process.pid, tools: [], toolMode: 'all' });
    await request('session/prompt', { sessionId: 'fixture', contentBlocks: [{ type: 'text', text: localModel ? 'Use computer tools to find the disposable fixture window, type Hello from Capri into its document text field, then inspect it again to verify. Do not save or close anything. Use accessibility elements; this is a synthetic test.' : 'List windows, subject to permission.' }] });
    await request('session/wait', { sessionId: 'fixture' });
    if (localModel) { assert(approvals >= 4, 'Local model did not complete the observation/input/verification loop'); assert(completedTools.some(m => JSON.stringify(m.content).includes('Hello from Capri')), 'Typed value never returned through verification'); assert(lastReply.includes('Hello from Capri'), 'Final reply did not confirm the typed value'); await request('shutdown'); child.stdin.end(); console.log('PASS local model completes a synthetic desktop workflow'); }
    else {
    assert.equal(approvals, fixture ? 4 : 1);
    assert.equal(requests.length, fixture ? 5 : 2);
    assert.equal(visuals, fixture ? 8 : 0);
    const names = requests[0].tools.map(t => t.function.name);
    assert.equal(names.filter(n => n.startsWith('computer_')).length, 7);
    assert(!names.includes('clipboard_read'));
    if (fixture) assert(requests.some(r => JSON.stringify(r).includes('image_url')), 'Vision attachment never reached the model');
    else assert(JSON.stringify(requests[1]).includes('declined'));
    await request('shutdown'); child.stdin.end();
    console.log(fixture ? 'PASS real pinned DSH chains window discovery, observation, click and verification using a synthetic driver' : 'PASS real pinned DSH initializes the native driver and exposes only seven computer tools');
    console.log(fixture ? 'PASS screenshot attachments reach the local vision model and every operation waits for host approval and visual acknowledgment' : 'PASS denied observation returns through the real multi-step model loop without reading desktop content');
    }
} finally {
    clearTimeout(timeout); server.close(); child.kill('SIGTERM');
    await new Promise(r => child.once('close', r)); rmSync(home, { recursive: true, force: true });
}
