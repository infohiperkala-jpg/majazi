import type { NextConfig } from "next";

const nextConfig: NextConfig = {
  poweredByHeader: false,
  reactStrictMode: true,
  async rewrites() {
    return [{ source: "/backend-api/:path*", destination: `${process.env.API_INTERNAL_URL ?? "http://api:8080"}/:path*` }];
  },
};

export default nextConfig;
