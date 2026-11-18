import type { Metadata } from "next";
import "./globals.css";
export const metadata: Metadata = { title: "MAJAZI | مرکز هوشمند بازاریابی و فروش", description: "سامانه یکپارچه مدیریت بازاریابی، محتوا و فروش" };
export default function RootLayout({ children }: Readonly<{ children: React.ReactNode }>) { return <html lang="fa" dir="rtl"><body>{children}</body></html>; }
