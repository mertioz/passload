import {mkdir, writeFile} from "node:fs/promises";
import {resolve, join} from "node:path";
import {pathToFileURL} from "node:url";

const binary = Buffer.alloc(2 * 1024 * 1024 + 100_003);
for (let i = 0; i < binary.length; i++) binary[i] = (i * 13 + (i >>> 7) + 17) % 251;
export const files = new Map([
  ["notes-test.txt", Buffer.from("Texte synthétique Passload.")],
  ["donnees-test.bin", binary],
  ["vide.txt", Buffer.alloc(0)]
]);

if (process.argv[1] && pathToFileURL(resolve(process.argv[1])).href === import.meta.url) {
  const output = resolve(process.argv[2] || ".");
  await mkdir(output,{recursive:true});
  for (const [name,bytes] of files) await writeFile(join(output,name),bytes,{flag:"wx"});
}
