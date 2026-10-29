import { readFileSync, readdirSync } from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
/** Percorso della cartella examples/ (sorella di dist/ dopo la build). */
export const EXAMPLES_DIR = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..", "..", "examples");
/** Nome file (senza estensione) -> oggetto JSON. */
export function loadExample(name) {
    const p = path.join(EXAMPLES_DIR, `${name}.json`);
    return JSON.parse(readFileSync(p, "utf8"));
}
/** Tutti i nomi degli esempi presenti in examples/. */
export function listExamples() {
    return readdirSync(EXAMPLES_DIR)
        .filter((f) => f.endsWith(".json"))
        .map((f) => f.slice(0, -5));
}
//# sourceMappingURL=helpers.js.map