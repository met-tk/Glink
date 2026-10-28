// Build-time dependency: sharp. Run with NODE_PATH pointing to its installed modules.
const fs = require('node:fs');
const path = require('node:path');
const sharp = require('sharp');

async function main() {
  const root = path.resolve(__dirname, '..');
  const assets = path.join(root, 'Moodia', 'Assets');
  const svg = fs.readFileSync(path.join(root, 'glink-symbol-monochrome.svg'), 'utf8')
    .replace('width="100%"', 'width="512"').replace('height="100%"', 'height="512"')
    .replaceAll('currentColor', '#FFFFFF')
    .replace(/(<svg[^>]+>)/, '$1<rect x="32" y="32" width="448" height="448" rx="104" fill="#171513"/>');
  const image = await sharp(Buffer.from(svg), { density: 288 }).resize(512, 512).png().toBuffer();
  fs.writeFileSync(path.join(__dirname, 'glink-app-icon-rounded.png'), image);
  const sizes = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256];
  const frames = await Promise.all(sizes.map(size => sharp(image).resize(size, size).png().toBuffer()));
  const header = Buffer.alloc(6 + 16 * sizes.length);
  header.writeUInt16LE(1, 2);
  header.writeUInt16LE(sizes.length, 4);
  let offset = header.length;
  frames.forEach((frame, i) => {
    const entry = 6 + i * 16;
    header[entry] = header[entry + 1] = sizes[i] % 256;
    header.writeUInt16LE(1, entry + 4);
    header.writeUInt16LE(32, entry + 6);
    header.writeUInt32LE(frame.length, entry + 8);
    header.writeUInt32LE(offset, entry + 12);
    offset += frame.length;
  });
  fs.writeFileSync(path.join(assets, 'AppIcon.ico'), Buffer.concat([header, ...frames]));
  const targets = {
    'Square44x44Logo.scale-200.png': [88, 88],
    'Square44x44Logo.targetsize-24_altform-unplated.png': [24, 24],
    'Square44x44Logo.targetsize-48_altform-lightunplated.png': [48, 48],
    'Square150x150Logo.scale-200.png': [300, 300],
    'StoreLogo.png': [50, 50],
    'LockScreenLogo.scale-200.png': [48, 48],
    'Wide310x150Logo.scale-200.png': [620, 300],
    'SplashScreen.scale-200.png': [1240, 600]
  };
  for (const [name, [width, height]] of Object.entries(targets))
    await sharp(image).resize(width, height, { fit: 'contain', background: '#00000000' }).png().toFile(path.join(assets, name));
  const { data, info } = await sharp(image).raw().toBuffer({ resolveWithObject: true });
  if (data[3] !== 0 || data[(256 * info.width + 32) * 4 + 3] !== 255)
    throw new Error('Icon must have transparent corners and an opaque rounded surface.');
  console.log('Rounded Windows assets generated from glink-symbol-monochrome.svg.');
}
main().catch(error => { console.error(error); process.exitCode = 1; });
