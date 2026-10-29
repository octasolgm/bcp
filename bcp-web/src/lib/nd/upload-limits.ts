/** A workspace's upload limits in MB (null = no limit) and its storage already used, from
 * GET /nd/workspaces/{id}/upload-limits. */
export type NdUploadLimits = {
  maxDocumentMb: number | null;
  maxUploadMb: number | null;
  maxStorageMb: number | null;
  usedStorageBytes: number;
};

const MB = 1024 * 1024;

export function formatMb(bytes: number): string {
  const mb = bytes / MB;
  return `${mb >= 10 ? Math.round(mb) : Math.round(mb * 10) / 10} MB`;
}

/**
 * Why the files picked for one upload cannot be uploaded, or null when they fit: each file under the
 * per-document limit, all together under the per-upload limit, and the library under its total storage
 * limit afterwards. The server checks the per-document and storage limits again on every file.
 */
export function checkUploadAgainstLimits(files: File[], limits: NdUploadLimits | null): string | null {
  if (!limits || !files.length) return null;
  const { maxDocumentMb, maxUploadMb, maxStorageMb, usedStorageBytes } = limits;

  if (maxDocumentMb) {
    const tooBig = files.filter((f) => f.size > maxDocumentMb * MB);
    if (tooBig.length) {
      const names = tooBig.map((f) => `${f.name} (${formatMb(f.size)})`).join(', ');
      return `Each document can be up to ${maxDocumentMb} MB. Too large: ${names}.`;
    }
  }
  const total = files.reduce((sum, f) => sum + f.size, 0);
  if (maxUploadMb && total > maxUploadMb * MB) {
    return `One upload can be up to ${maxUploadMb} MB in total; these ${files.length} files are ${formatMb(total)}. Choose fewer files.`;
  }
  if (maxStorageMb && usedStorageBytes + total > maxStorageMb * MB) {
    const left = Math.max(0, maxStorageMb * MB - usedStorageBytes);
    return `Not enough storage left: ${formatMb(left)} of ${maxStorageMb} MB is free and this upload is ${formatMb(total)}.`;
  }
  return null;
}
