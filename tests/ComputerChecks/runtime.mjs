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
const toolCalls = [];
let lastReply = "";
let reasoningChars = 0;
const readOnly = process.argv.includes('--read-only');
const calculator = process.argv.includes('--calculator');
const generalRoute = process.argv.includes('--general-route');
const expectedValue = readOnly ? '0' : calculator ? '7' : 'Hello from Capri';
const localModel = process.argv.includes('--local-model');
const deniedComputer = process.argv.includes('--computer-denied');
const noTools = process.argv.includes('--no-tools');
const neverTools = process.argv.includes('--never-tools');
const fixture = process.argv.includes('--fixture') || localModel || noTools || neverTools;
let catalog;
if (localModel) {
    const { CuaDriver } = await import('../../out/dsh-runtime/node_modules/@trycua/cua-driver/dist/index.js');
    const driver = CuaDriver.create();
    try { catalog = await driver.listToolsJson(); } finally { await driver.shutdown(); driver.uniffiDestroy(); }
}
let runtime = resolve('out/dsh-runtime');
const target = { pid: 8124, window_id: 42 };
const plan = [ ['computer_list_windows', {}], ['computer_get_window_state', target], ['computer_click', { ...target, element_index: 1 }], ['computer_get_window_state', target] ];
if (fixture) {
    runtime = join(home, 'runtime');
    const driver = join(runtime, 'node_modules', '@trycua', 'cua-driver');
    mkdirSync(join(driver, 'dist'), { recursive: true });
    symlinkSync(resolve('out/dsh-runtime/node_modules/@deepseek-ai'), join(runtime, 'node_modules', '@deepseek-ai'));
    writeFileSync(join(runtime, 'package.json'), '{"private":true}');
    writeFileSync(join(driver, 'package.json'), '{"version":"0.28.0","type":"module"}');
    writeFileSync(join(driver, 'dist', 'index.js'), `
    const tools = ${JSON.stringify(['list_apps', 'list_windows', 'get_window_state', 'click', 'type_text', 'press_key', 'scroll'])};
    let value = ${JSON.stringify(readOnly || calculator ? "0" : "")};
    export class CuaDriver {
        static create() { return new CuaDriver(); }
        async listToolsJson() { if (${JSON.stringify(catalog ?? null)}) return ${JSON.stringify(catalog ?? null)}; return JSON.stringify({ tools: tools.map(name => ({ name, inputSchema: { type: 'object', properties: { pid: {type:'integer'}, window_id: {type:'integer'}, element_token: {type:'string'} } } })) }); }
        async callTool(name, raw) {
            if (name === 'click' && JSON.parse(raw).element_index != null && JSON.parse(raw).snapshot_id !== 's00000001') throw new Error('Index click did not carry the current snapshot');
            if (name === "type_text") value = JSON.parse(raw).text;
            if (${calculator} && name === 'click') { const a = JSON.parse(raw); if (a.element_token !== 'seven' && a.element_index !== 5) throw new Error('Wrong Calculator target'); value = '7'; }
            const bounds = { x:100, y:100, width:160, height:80 };
            const structuredContent = name === 'check_permissions' ? { accessibility:true,screen_recording:true } : name === 'list_windows' ? { windows: [{pid:8124,window_id:42,app_name:${JSON.stringify(readOnly || calculator ? 'Calculator' : 'Disposable fixture')},title:${JSON.stringify(readOnly || calculator ? 'Calculator' : 'Computer test')},is_on_screen:true,on_current_space:true,bounds}] } : name === 'get_window_state' ? {pid:8124,window_id:42,window_bounds:bounds,snapshot_id:'s00000001',elements:[...(${calculator} ? [{element_index:5,element_token:'seven',role:'AXButton',label:'7',enabled:true,frame:{x:120,y:150,w:40,h:20}}] : []),{element_index:1,element_token:'fixture',role:${JSON.stringify(readOnly ? 'AXStaticText' : 'AXTextArea')},label:${JSON.stringify(readOnly ? 'Display' : 'Document text')},value,frame:{x:120,y:120,w:40,h:20}}]} : {effect:'delivered'};
            const content = [{type:'text',text:JSON.stringify(structuredContent)}];
            if (name === 'get_window_state' && JSON.parse(raw || '{}').include_screenshot !== false) content.push({type:'image',mimeType:'image/png',data:${JSON.stringify(readFileSync('assets/cursor.png').toString('base64'))}});
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
                for (const choice of part.choices ?? []) { reasoningChars += (choice.delta?.reasoning_content ?? '').length; if (choice.finish_reason) console.log('MODEL finish:', choice.finish_reason); for (const tool of choice.delta?.tool_calls ?? []) if (tool.function?.name) console.log('MODEL tool:', tool.function.name); }
            }
        }
        res.statusCode = response.status; res.setHeader('Content-Type', response.headers.get('content-type')); res.end(body); return;
    }
    if (req.url === '/api/show') { res.setHeader('Content-Type', 'application/json'); res.end(JSON.stringify({ capabilities: ['completion', 'tools', 'vision'] })); return; }
    requests.push(JSON.parse(raw));
    const planStep = step - (noTools ? 1 : 0);
    const skipped = neverTools || noTools && step === 0;
    const planned = !skipped && fixture && planStep >= 0 && planStep < plan.length ? plan[planStep] : !fixture && step === 0 ? ['computer_list_windows', {}] : null;
    step++;
    const delta = planned ? { role: 'assistant', tool_calls: [{ index: 0, id: 'computer-fixture', type: 'function', function: { name: planned[0], arguments: JSON.stringify(planned[1]) } }] } : { role: 'assistant', content: skipped ? 'Mac is still blocking me. Enable permissions and restart Capri.' : fixture ? 'Observed the fixture after clicking.' : 'Permission was declined; no desktop was read.' };
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
    if (frame.method === 'capri/approval') { approvals++; assert(frame.params.tool.startsWith('computer_')); if (readOnly) assert(['computer_list_apps','computer_list_windows','computer_get_window_state'].includes(frame.params.tool)); send({ id: frame.id, result: { allowed: fixture } }); }
    else if (frame.method === 'capri/computer') { assert(fixture, 'Denied desktop observation reached input admission'); visuals++; send({ id: frame.id, result: {ready:true} }); }
    else if (localModel && frame.method === 'session.event') { const event=frame.params.event; if (event.type === 'tool/call') toolCalls.push(event.data.name); if (event.type === 'turn/end') console.log('TURN END',JSON.stringify(event.data)); if (event.type === 'tool/result') completedTools.push(event.data.message); if (event.type === 'assistant/message') { lastReply = event.data.message.content.filter(b=>b.type==='text').map(b=>b.text).join(''); console.log('ASSISTANT',JSON.stringify(event.data.message.content.filter(b=>b.type==='text'||b.type==='tool-call')).slice(0,1000)); } }
    else if (frame.id != null && waiting.has(frame.id)) { const w = waiting.get(frame.id); waiting.delete(frame.id); frame.error ? w.reject(new Error(JSON.stringify(frame.error))) : w.resolve(frame.result); }
});
child.on('exit', code => { for (const w of waiting.values()) w.reject(new Error('Bridge exited ' + code + ': ' + errors)); });
const timeout = setTimeout(() => child.kill('SIGTERM'), 45000);
try {
    await request('capri/initialize', { cwd: workspace, port: server.address().port, model: 'qwen3.5:9b', persona: 'Test fixture only.', computer: true, readOnlyComputer: readOnly, hostPid: process.pid, tools: [], toolMode: !generalRoute && (localModel || noTools || neverTools || deniedComputer) ? 'computer' : 'all' });
    await request('session/prompt', { sessionId: 'fixture', contentBlocks: [{ type: 'text', text: localModel && readOnly ? 'Earlier assistant messages kept saying macOS blocked permissions. Current request: Use computer tools to inspect Calculator and tell me its displayed number. Don’t click or type.' : localModel && calculator ? 'Use computer tools to inspect Calculator, click the 7 button exactly once, then inspect it again to verify the display. Don’t use keyboard shortcuts.' : localModel ? 'Earlier assistant replies repeatedly claimed macOS blocked all desktop permissions; those claims are old and unverified. Current request: Use computer tools to find the disposable fixture window, type exactly "Hello from Capri" into its document text field, then inspect it again to verify. Do not save or close anything. Use accessibility elements; this is a synthetic test.' : 'List windows, subject to permission.' }] });
    let failure;
    try { await request('session/wait', { sessionId: 'fixture' }); } catch (error) { if (!neverTools) throw error; failure = error.message; }
    assert(requests.every(r => r.chat_template_kwargs?.enable_thinking === false), 'Qwen with computer use enabled must disable thinking in both natural and explicit computer routes'); console.log('PASS thinking-off reaches Qwen with computer use enabled, including the general DSH route');
    if (localModel) { assert.equal(reasoningChars, 0, 'Local model still emitted reasoning despite thinking-off setting'); console.log('PASS local Qwen completes requests without emitting reasoning'); }
    if (neverTools) { assert(failure?.includes('No computer tool ran')); assert.equal(requests.length, 2); assert.equal(approvals, 0); console.log('PASS ungrounded replies get one correction then a clear no-action error without input or approvals'); }
    if (neverTools) { await request('shutdown'); child.stdin.end(); }
    else if (localModel) { assert(requests.every(r => r.max_tokens === 16384), 'Computer turns did not receive the larger output budget'); assert(approvals >= (readOnly ? 2 : 4), 'Local model did not complete the observation/input/verification loop'); assert(completedTools.some(m => JSON.stringify(m.content).includes(readOnly ? 'Display' : expectedValue)), 'Typed value never returned through verification'); assert(lastReply.includes(expectedValue), 'Final reply did not confirm the typed value'); await request('shutdown'); child.stdin.end(); if (calculator) { assert.deepEqual(toolCalls.filter(n => !['computer_list_apps','computer_list_windows','computer_get_window_state'].includes(n)), ['computer_click']); const at = toolCalls.indexOf('computer_click'); assert.equal(toolCalls[at - 1], 'computer_get_window_state'); assert.equal(toolCalls[at + 1], 'computer_get_window_state'); } console.log(readOnly ? 'PASS local Qwen ignores stale permission claims and reads the synthetic Calculator display using observation-only tools' : 'PASS local model completes a synthetic desktop workflow'); }
    else {
    assert.equal(approvals, fixture ? 4 : 1);
    assert.equal(requests.length, fixture ? noTools ? 6 : 5 : 2);
    if (noTools) { assert(requests[1].messages.some(m => JSON.stringify(m).includes("No tools ran in your last reply"))); assert(!requests[0].tools.some(t => ["bash","read","write","edit"].includes(t.function.name))); console.log("PASS stale no-tool permission excuse triggers one real observation attempt with unrelated tools excluded"); }
    assert.equal(visuals, fixture ? 8 : 0);
    assert(requests.every(r => r.max_tokens === 16384), 'Computer turns did not receive the larger output budget');
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
