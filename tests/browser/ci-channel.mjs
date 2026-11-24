const id = process.env.GITHUB_RUN_ID;
const attempt = process.env.GITHUB_RUN_ATTEMPT;
export const isWorkflow = /^\d+$/.test(id || "") && /^\d+$/.test(attempt || "");

// Only an expiring invitation to the publicly reproducible CI fixtures is exchanged.
// The sending machine and downloading machines are independent ephemeral runners.
export async function testChannel(browser, stage, message) {
  if (!isWorkflow || !["chromium","firefox","webkit"].includes(browser) || !["invite","done"].includes(stage))
    throw new Error("Canal de test inconnu.");
  const path = "https://ppng.io/passload-ci/" + id + "-" + attempt + "/" + browser + "/" + stage;
  const until = Date.now() + 5 * 60_000;
  do {
    try {
      const res = await fetch(path,{method:message ? "POST" : "GET",body:message,cache:"no-store",signal:AbortSignal.timeout(55_000)});
      if (!res.ok) throw new Error("Canal occupé.");
      const text = await res.text();
      if (text.length > 2048 || text.includes("[ERROR]")) throw new Error("Réponse de test invalide.");
      return text.trim();
    } catch { await new Promise(resolve => setTimeout(resolve,750)); }
  } while (Date.now() < until);
  throw new Error("L’autre machine de test n’a pas répondu.");
}
