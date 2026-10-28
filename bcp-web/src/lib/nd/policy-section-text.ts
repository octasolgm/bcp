/** Strip Landing parse artifacts so section previews look like extract text. */
export function sanitizePolicySectionText(text: string | null | undefined): string {
  if (!text) return '';
  return text
    .replace(/<a\s+id\s*=\s*['"][^'"]+['"]\s*(?:\/>|>\s*<\/a>)/gi, '')
    .replace(/P\s*a\s*g\s*e\s*\|\s*\d+/gi, '')
    .replace(/^\s*[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\s*$/gim, '')
    .replace(/\n{3,}/g, '\n\n')
    .trim();
}
