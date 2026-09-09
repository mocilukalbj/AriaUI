import fs from 'node:fs';
import path from 'node:path';
import crypto from 'node:crypto';
import zlib from 'node:zlib';
import { fileURLToPath } from 'node:url';

const __filename = fileURLToPath(import.meta.url);
const __dirname = path.dirname(__filename);

const repoRoot = path.resolve(__dirname, '..');
const logFile = path.join(repoRoot, 'pack.log');
fs.writeFileSync(logFile, 'Packaging started...\n');
const log = msg => fs.appendFileSync(logFile, msg + '\n');

process.on('uncaughtException', err => {
    console.error('Packaging failed with uncaught exception:', err?.message || err);
    try {
        fs.appendFileSync(logFile, 'Uncaught error: ' + (err?.stack || err) + '\n');
    } catch {}
    process.exit(1);
});

process.on('unhandledRejection', reason => {
    console.error('Packaging failed with unhandled rejection:', reason?.message || reason);
    try {
        fs.appendFileSync(logFile, 'Unhandled rejection: ' + (reason?.stack || reason) + '\n');
    } catch {}
    process.exit(1);
});

const pluginDir = path.join(repoRoot, 'undone_plugin');
const keyPath = process.env.ARIAUI_KEY_PATH || path.join(repoRoot, '..', 'work', 'extension-signing', 'ariaui-companion.pem');
const outputDir = path.join(repoRoot, '..', 'outputs', 'browser-extension');
const unpackedDir = path.join(outputDir, 'AriaUI-Companion');

// 1. Read private key and calculate SPKI & extension ID
if (!fs.existsSync(keyPath)) {
    throw new Error(`Private key not found at ${keyPath}`);
}
log('Reading key...');
const pem = fs.readFileSync(keyPath, 'utf8');
const privateKey = crypto.createPrivateKey(pem);
const publicKey = crypto.createPublicKey(privateKey);
const spki = publicKey.export({ type: 'spki', format: 'der' });

const idBytes = crypto.createHash('sha256').update(spki).digest().subarray(0, 16);
let extensionId = '';
for (const b of idBytes) {
    extensionId += String.fromCharCode(97 + (b >> 4)) + String.fromCharCode(97 + (b & 0x0f));
}

console.log(`Extension ID: ${extensionId}`);
if (extensionId !== 'ehicmejmhlcflckpndnpmedmfkopcdfb') {
    throw new Error(`Extension ID mismatch! Expected ehicmejmhlcflckpndnpmedmfkopcdfb, got ${extensionId}`);
}

// 2. Read manifest and add public key
const manifestRaw = fs.readFileSync(path.join(pluginDir, 'manifest.json'), 'utf8');
const manifestObj = JSON.parse(manifestRaw);
manifestObj.key = spki.toString('base64');
const manifestFormatted = JSON.stringify(manifestObj, null, 2) + '\n';
const version = manifestObj.version;

// 3. Collect files to include in package
const filesToPack = [
    'background.js',
    'options.html',
    'README.md',
    'README.cn.md',
    'LINUX_TESTING.md',
    'LICENSE',
    'acknowledgment.txt',
    'css/companion.css',
    'images/logo16.png',
    'images/logo32.png',
    'images/logo48.png',
    'images/logo128.png'
];

const jsDir = path.join(pluginDir, 'js');
for (const f of fs.readdirSync(jsDir)) {
    if (f.endsWith('.js')) {
        filesToPack.push('js/' + f);
    }
}

// 4. Build ZIP archive in memory using standard ZIP format
function crc32(buf) {
    let crc = 0 ^ -1;
    for (let i = 0; i < buf.length; i++) {
        crc = (crc >>> 8) ^ crcTable[(crc ^ buf[i]) & 0xFF];
    }
    return (crc ^ -1) >>> 0;
}
const crcTable = new Uint32Array(256);
for (let i = 0; i < 256; i++) {
    let c = i;
    for (let k = 0; k < 8; k++) {
        c = (c & 1) ? (0xEDB88320 ^ (c >>> 1)) : (c >>> 1);
    }
    crcTable[i] = c >>> 0;
}

const entries = [];
// Add manifest.json
const manifestBuf = Buffer.from(manifestFormatted, 'utf8');
entries.push({ name: 'manifest.json', data: manifestBuf });

// Add other files
for (const rel of filesToPack) {
    const src = path.join(pluginDir, rel);
    const data = fs.readFileSync(src);
    entries.push({ name: rel.replace(/\\/g, '/'), data });
}

const localHeaders = [];
const centralHeaders = [];
let offset = 0;

for (const entry of entries) {
    const nameBuf = Buffer.from(entry.name, 'utf8');
    const crc = crc32(entry.data);
    const deflated = zlib.deflateRawSync(entry.data);
    const useDeflate = deflated.length < entry.data.length;
    const compData = useDeflate ? deflated : entry.data;
    const method = useDeflate ? 8 : 0;

    // Local header
    const lh = Buffer.alloc(30 + nameBuf.length);
    lh.writeUInt32LE(0x04034b50, 0); // signature
    lh.writeUInt16LE(20, 4);          // version needed
    lh.writeUInt16LE(0, 6);           // flags
    lh.writeUInt16LE(method, 8);      // compression method
    lh.writeUInt16LE(0, 10);          // time
    lh.writeUInt16LE(0x5460, 12);     // date
    lh.writeUInt32LE(crc, 14);        // crc32
    lh.writeUInt32LE(compData.length, 18); // comp size
    lh.writeUInt32LE(entry.data.length, 22); // uncomp size
    lh.writeUInt16LE(nameBuf.length, 26);  // filename len
    lh.writeUInt16LE(0, 28);               // extra len
    nameBuf.copy(lh, 30);

    // Central header
    const ch = Buffer.alloc(46 + nameBuf.length);
    ch.writeUInt32LE(0x02014b50, 0); // signature
    ch.writeUInt16LE(20, 4);          // version made by
    ch.writeUInt16LE(20, 6);          // version needed
    ch.writeUInt16LE(0, 8);           // flags
    ch.writeUInt16LE(method, 10);     // compression method
    ch.writeUInt16LE(0, 12);          // time
    ch.writeUInt16LE(0x5460, 14);     // date
    ch.writeUInt32LE(crc, 16);        // crc32
    ch.writeUInt32LE(compData.length, 20); // comp size
    ch.writeUInt32LE(entry.data.length, 24); // uncomp size
    ch.writeUInt16LE(nameBuf.length, 28);  // filename len
    ch.writeUInt16LE(0, 30);               // extra len
    ch.writeUInt16LE(0, 32);               // comment len
    ch.writeUInt16LE(0, 34);               // disk start
    ch.writeUInt16LE(0, 36);               // internal attr
    ch.writeUInt32LE(0, 38);               // external attr
    ch.writeUInt32LE(offset, 42);          // local header offset
    nameBuf.copy(ch, 46);

    localHeaders.push(lh, compData);
    centralHeaders.push(ch);
    offset += lh.length + compData.length;
}

const centralDirOffset = offset;
const centralDirBuf = Buffer.concat(centralHeaders);
const eocd = Buffer.alloc(22);
eocd.writeUInt32LE(0x06054b50, 0); // signature
eocd.writeUInt16LE(0, 4);          // disk number
eocd.writeUInt16LE(0, 6);          // disk with cd
eocd.writeUInt16LE(entries.length, 8);  // entries on disk
eocd.writeUInt16LE(entries.length, 10); // total entries
eocd.writeUInt32LE(centralDirBuf.length, 12); // cd size
eocd.writeUInt32LE(centralDirOffset, 16);    // cd offset
eocd.writeUInt16LE(0, 20);                  // comment len

const zipBuffer = Buffer.concat([...localHeaders, centralDirBuf, eocd]);
fs.mkdirSync(outputDir, { recursive: true });
const zipPath = path.join(outputDir, `AriaUI-Companion-${version}.zip`);
fs.writeFileSync(zipPath, zipBuffer);
console.log(`Saved ZIP archive: ${zipPath} (${zipBuffer.length} bytes)`);

// 6. Build CRX3
function varint(value) {
    const list = [];
    while (value >= 128) {
        list.push((value & 127) | 128);
        value = value >>> 7;
    }
    list.push(value);
    return Buffer.from(list);
}

function field(num, buf) {
    const tag = varint((num << 3) | 2);
    const len = varint(buf.length);
    return Buffer.concat([tag, len, buf]);
}

const signedHeader = field(1, idBytes);
const signedDataPrefix = Buffer.from('CRX3 SignedData\0', 'ascii');
const signedHeaderLen = Buffer.alloc(4);
signedHeaderLen.writeUInt32LE(signedHeader.length, 0);

const signedBytes = Buffer.concat([signedDataPrefix, signedHeaderLen, signedHeader, zipBuffer]);

const signer = crypto.createSign('SHA256');
signer.update(signedBytes);
const signature = signer.sign({ key: privateKey, dsaEncoding: 'der' });

// Verify signature
const verifier = crypto.createVerify('SHA256');
verifier.update(signedBytes);
if (!verifier.verify(publicKey, signature)) {
    throw new Error('CRX3 signature verification failed!');
}
console.log('CRX3 signature successfully verified with public key.');

const proof = Buffer.concat([field(1, spki), field(2, signature)]);
const header = Buffer.concat([field(3, proof), field(10000, signedHeader)]);

const crxMagic = Buffer.from('Cr24', 'ascii');
const crxVersion = Buffer.alloc(4); crxVersion.writeUInt32LE(3, 0);
const crxHeaderLen = Buffer.alloc(4); crxHeaderLen.writeUInt32LE(header.length, 0);

const crxBuffer = Buffer.concat([crxMagic, crxVersion, crxHeaderLen, header, zipBuffer]);
const crxPath = path.join(outputDir, `AriaUI-Companion-${version}.crx`);
fs.writeFileSync(crxPath, crxBuffer);
console.log(`Saved CRX3 package: ${crxPath} (${crxBuffer.length} bytes)`);

// Write extension-id.txt
fs.writeFileSync(path.join(outputDir, 'extension-id.txt'), extensionId + '\n');
console.log(`Updated extension-id.txt with ID: ${extensionId}`);

// 6. Update unpacked directory if explicitly requested
const shouldUpdateUnpacked = process.argv.includes('--update-unpacked');
if (shouldUpdateUnpacked) {
    fs.mkdirSync(unpackedDir, { recursive: true });
    fs.writeFileSync(path.join(unpackedDir, 'manifest.json'), manifestFormatted);

    for (const rel of filesToPack) {
        const src = path.join(pluginDir, rel);
        const dest = path.join(unpackedDir, rel);
        fs.mkdirSync(path.dirname(dest), { recursive: true });
        fs.copyFileSync(src, dest);
    }
    console.log(`Updated unpacked directory: ${unpackedDir}`);
} else {
    console.log('Skipped updating unpacked directory (use --update-unpacked to sync actual loaded directory).');
}

console.log('Packaging complete!');
