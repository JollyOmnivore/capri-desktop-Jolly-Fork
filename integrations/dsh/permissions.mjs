import { readFileSync } from 'node:fs';
import { join, resolve } from 'node:path';
import { pathToFileURL } from 'node:url';
const root = resolve(process.argv[2]);
const pkg = join(root, 'node_modules', '@trycua', 'cua-driver');
if (JSON.parse(readFileSync(join(pkg, 'package.json'), 'utf8')).version !== '0.28.0') throw new Error('Reinstall the pinned Capri desktop driver');
const { CuaDriver } = await import(pathToFileURL(join(pkg, 'dist', 'index.js')).href);
const driver = CuaDriver.create();
try {
    const result = JSON.parse((await driver.callTool('check_permissions', JSON.stringify({ prompt: false, probe_direct_capture: false }))).rawJson);
    console.log(JSON.stringify(result.structuredContent ?? { error: 'Driver returned no permission status' }));
} finally { await driver.shutdown(); driver.uniffiDestroy(); }
