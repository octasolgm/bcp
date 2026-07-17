export default function OldCatchAll() {
  const webUrl = process.env.BCP_WEB_URL;
  return (
    <div className="flex min-h-screen items-center justify-center p-8">
      <p className="text-[var(--text-muted)]">
        Loading legacy app via proxy… If this persists, ensure bcp-web is running and{" "}
        <code className="text-xs">BCP_WEB_URL</code>
        {webUrl ? ` (${webUrl})` : " is configured"}.
      </p>
    </div>
  );
}
