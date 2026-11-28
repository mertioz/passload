const response = await fetch(new URL("./locales.json", import.meta.url));
const catalogs = await response.json();
const byLanguage = new Map(Object.keys(catalogs).map(tag => [tag.split("-")[0], tag]));
const root = document.documentElement;
const match = tag => byLanguage.get(tag?.toLowerCase().split("-")[0]);
export const language = match(root.dataset.locale) ??
  (navigator.languages ?? [navigator.language]).map(match).find(Boolean) ?? "en-US";
root.lang = language;

export const text = (key, ...values) =>
  (catalogs[language][key] ?? catalogs["en-US"][key]).replace(/\{(\d+)\}/g, (_, index) => values[index]);

const units = text("ByteUnits").split(" ");
const numbers = new Intl.NumberFormat(language, {maximumFractionDigits:1});

export function formatSize(bytes) {
  let unit = 0;
  while (bytes >= 1024 && unit < units.length - 1) { bytes /= 1024; unit++; }
  return numbers.format(bytes) + " " + units[unit];
}
