import { spawnSync } from 'node:child_process';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const testDir = path.dirname(fileURLToPath(import.meta.url));
const testFiles = [
    'extension-request-observer.mjs',
    'extension-context-menu.mjs',
    'extension-settings.mjs',
    'extension-handoff.mjs'
];

let allPassed = true;
for (const file of testFiles) {
    const fullPath = path.join(testDir, file);
    const res = spawnSync(process.execPath, [fullPath], {
        stdio: 'inherit',
        env: process.env
    });
    if (res.status !== 0) {
        console.error(`FAIL: ${file} (exit code ${res.status})`);
        allPassed = false;
    }
}

if (!allPassed) {
    process.exit(1);
}

const shouldPack = process.argv.includes('--pack');
const shouldUpdateUnpacked = process.argv.includes('--update-unpacked');

if (shouldPack || shouldUpdateUnpacked) {
    console.log('\nPackaging extension...');
    const packArgs = [path.join(testDir, '..', 'packaging', 'pack_extension.mjs')];
    if (shouldUpdateUnpacked) packArgs.push('--update-unpacked');
    const packRes = spawnSync(process.execPath, packArgs, {
        stdio: 'inherit',
        env: process.env
    });
    if (packRes.status !== 0) {
        console.error(`FAIL: packaging failed (exit code ${packRes.status})`);
        process.exit(1);
    }
    console.log('\nALL EXTENSION TESTS AND PACKAGING PASSED SUCCESSFULLY!');
} else {
    console.log('\nALL EXTENSION TESTS PASSED SUCCESSFULLY!');
}
