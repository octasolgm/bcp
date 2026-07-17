import type { NextConfig } from "next";

const bcpWebUrl = process.env.BCP_WEB_URL;

const nextConfig: NextConfig = {
  async rewrites() {
    if (!bcpWebUrl) {
      return [];
    }
    return [
      {
        source: "/old/:path*",
        destination: `${bcpWebUrl.replace(/\/$/, "")}/:path*`,
      },
    ];
  },
};

export default nextConfig;
