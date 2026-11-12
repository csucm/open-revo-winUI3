// Regenerate after updating the shared quick-button SVGs. Usage: node generate.cjs
// Requires sharp. The application build embeds these rasters directly.
const path = require("node:path");
const fs = require("node:fs/promises");
const sharp = require(process.argv[2] || "sharp");
const assets = path.resolve(__dirname, "../../../../OpenRevo.Native/Assets/Approved");
const toggles = ["awake", "bluetooth", "camera", "caps", "fan", "fn-lock", "mic",
    "touchpad", "wifi", "win-lock"];
const staticIcons = ["display", "info", "mode-balanced", "mode-custom", "mode-office", "mode-performance"];

async function main() {
    const icons = [...staticIcons, ...toggles.flatMap(name => [name, name + "-off"])];
    for (const name of icons) {
        const source = name.endsWith("-off")
            ? name.slice(0, -4) + "-dark-compact-off.svg"
            : name + "-dark-compact.svg";
        const {data, info} = await sharp(path.join(assets, source), {density: 144})
            .ensureAlpha().raw().toBuffer({resolveWithObject: true});
        if (info.width !== 48 || info.height !== 48 || info.channels !== 4) {
            throw new Error("Unexpected quick icon dimensions for " + source);
        }
        await fs.writeFile(path.join(__dirname, name + ".rgba"), data);
    }
}
main().catch(error => { console.error(error); process.exitCode = 1; });

