import { createRequire } from 'node:module';
import { pathToFileURL } from 'node:url';
import { mkdirSync, writeFileSync, realpathSync, existsSync, lstatSync, readFileSync } from 'node:fs';
import { resolve, dirname, relative, isAbsolute, join } from 'node:path';

const root = resolve(process.argv[2]);
const require = createRequire(join(root, 'package.json'));
for (const name of ['dsh', 'dsh-llm-pi-ai', 'dsh-sdk-jsonrpc-server', 'dsh-tools', 'dsh-fs-sandbox', 'dsh-bash-sandbox', 'dsh-sandbox-local']) {
    if (JSON.parse(readFileSync(join(root, 'node_modules', '@deepseek-ai', name, 'package.json'), 'utf8')).version !== '0.2.1-rc.5') throw new Error('Install the pinned Capri DSH runtime with npm ci');
}
const load = name => import(pathToFileURL(require.resolve(name)).href);
const { JsonRpcLineTransport } = await load('@deepseek-ai/dsh-sdk-protocol');
const { HarnessSdkJsonRpcServer } = await load('@deepseek-ai/dsh-sdk-jsonrpc-server');
const { runProfile } = await load('@deepseek-ai/dsh/profile-boot');
const { createLaunchEnvironmentSnapshot } = await load('@deepseek-ai/dsh-launch-environment');
const transport = new JsonRpcLineTransport(process.stdin, process.stdout);
let server, runtime, initialized = false, calls = 0;
const deny = reason => ({ kind: 'deny', reason });

function confinedPath(workspace, path) {
    if (typeof path !== 'string' || !path.trim()) return false;
    let target = resolve(workspace, path);
    while (!existsSync(target)) {
        const parent = dirname(target);
        if (parent === target) return false;
        target = parent;
    }
    const physical = realpathSync(target);
    const rest = relative(workspace, physical);
    return !isAbsolute(rest) && rest !== '..' && !rest.startsWith('../');
}

async function initialize(params) {
    if (initialized) throw new Error('Already initialized');
    initialized = true;
    if (!Number.isInteger(params.port) || params.port < 1 || params.port > 65535) throw new Error('Invalid loopback port');
    if (!/^qwen[\w.:-]*$/i.test(params.model ?? '') && !/^[\w.][\w.\/:+-]{0,199}$/.test(params.model ?? '')) throw new Error('Invalid local model');
    const workspace = realpathSync(params.cwd);
    if (lstatSync(params.cwd).isSymbolicLink()) throw new Error('Choose a workspace directory rather than a symlink');
    const home = process.env.DSH_HOME;
    mkdirSync(home, { recursive: true, mode: 0o700 });
    const disabled = ['sdk-app-startup', 'sdk-jsonrpc-server', 'deepseek-llm-api-extensions', 'session-log-deepseek', 'plugin-package-inventory-deepseek', 'llm-deepseek', 'filesystem', 'tool-working-directory', 'terminal-bash', 'terminal-pwsh', 'persistent-bash', 'persistent-pwsh'];
    const patches = disabled.map(id => ({ id, disabled: true }));
    patches.push({ id: 'sandbox-policy', config: { mode: 'workspace-write', workspaceRoot: workspace } });
    patches.push({ id: 'tool-fs', config: { read: true, readImage: false, write: true, edit: true, readMaxBytes: 65536 } });
    patches.push({ id: 'system-prompt', config: { includeHarnessIdentity: false, includeRuntimeContext: false, personaPrefix: params.persona + '\nUse only the supplied tools. Every tool needs user approval. Respect denied operations and never retry them using a different tool. Do not claim an operation ran before receiving its result.' } });
    patches.push({ insert: [
        { id: 'capri-filesystem', name: '@deepseek-ai/dsh-fs-sandbox' },
        { id: 'capri-shell', name: '@deepseek-ai/dsh-bash-sandbox', config: { cwd: workspace, timeoutMs: 30000, maxTimeoutMs: 30000, maxOutputBytes: 65536, maxSpillBytes: 65536 } },
        { id: 'capri-shell-env', name: '@deepseek-ai/dsh-shell-env' },
        { id: 'capri-bash-tool', name: '@deepseek-ai/dsh-tool-bash', config: { enableRunInBackground: false, promoteOnTimeout: false } },
        { id: 'capri-local-model', name: '@deepseek-ai/dsh-llm-pi-ai', config: { providers: { 'capri-local': {
            api: 'openai-completions', baseURL: `http://127.0.0.1:${params.port}/v1`, apiKeyEnv: 'CAPRI_LOCAL_MODEL_KEY',
            models: [{ id: params.model, contextWindow: 32768, maxTokens: 4096, input: ['text'] }],
            compat: { supportsDeveloperRole: false, maxTokensField: 'max_tokens' },
            timeoutMs: 120000, streamIdleTimeoutMs: 120000, retryPolicy: { mode: 'normal', maxRetries: 0 }
        } } } }
    ] });
    const patch = join(home, 'capri.patch.json');
    writeFileSync(patch, JSON.stringify(patches), { mode: 0o600 });
    runtime = await runProfile({ profile: 'sdk-minimal', patchFiles: [patch], args: [], environment: createLaunchEnvironmentSnapshot([{ source: 'process', values: { ...process.env } }]) });
    runtime.ctx.on('tools/pre-execute', async (exec, next) => {
        if (!['read', 'write', 'edit', 'bash'].includes(exec.name)) return deny('Tool is not enabled in Capri');
        if (exec.arguments?.sandbox_permissions && exec.arguments.sandbox_permissions !== 'workspace-write') return deny('Sandbox escalation is not enabled in Capri');
        if (++calls > 12) return deny('Tool limit reached; ask the user to continue in another turn');
        if (exec.name !== 'bash' && !confinedPath(workspace, exec.arguments?.file_path)) return deny('File must be inside the selected workspace');
        if (exec.signal.aborted) return { kind: 'cancel' };
        try {
            const answer = await transport.request('capri/approval', { tool: exec.name, arguments: exec.arguments, cwd: workspace }, exec.signal);
            if (exec.signal.aborted) return { kind: 'cancel' };
            if (answer?.allowed !== true) return deny('User declined this operation. Do not retry it.');
            if (exec.name !== 'bash' && !confinedPath(workspace, exec.arguments?.file_path)) return deny('File path changed outside the workspace');
            return await next();
        } catch { return deny('Approval unavailable or cancelled'); }
    });
    server = new HarnessSdkJsonRpcServer(runtime.ctx, transport, { maxTokensAsSuccess: false });
    return server.initialize({ cwd: workspace, provider: 'capri-local', model: params.model, maxTokens: 4096 });
}

transport.onRequest(async (method, params) => {
    if (method === 'capri/initialize') return initialize(params);
    if (!server) throw new Error('Initialize Capri first');
    if (!['session/prompt', 'session/wait', 'shutdown'].includes(method)) throw new Error('Unsupported Capri method');
    const result = await server.handleRequest(method, params);
    if (method === 'shutdown') setImmediate(() => runtime.shutdown.shutdown(0));
    return result;
});
process.stdin.on('end', () => { transport.close(); runtime?.shutdown.shutdown(0); });
transport.start();
