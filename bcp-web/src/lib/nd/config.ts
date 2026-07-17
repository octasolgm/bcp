export function getPublicApiUrl(): string {
  const url = process.env.NEXT_PUBLIC_BCP_API_URL;
  if (!url) {
    throw new Error("NEXT_PUBLIC_BCP_API_URL is not configured");
  }
  return url.replace(/\/$/, "");
}

export function getPublicAppUrl(): string {
  const url = process.env.NEXT_PUBLIC_APP_URL;
  if (!url) {
    throw new Error("NEXT_PUBLIC_APP_URL is not configured");
  }
  return url.replace(/\/$/, "");
}
