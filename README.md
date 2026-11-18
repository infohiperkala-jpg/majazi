# MAJAZI — مرکز هوشمند بازاریابی و فروش

سامانه چندسازمانی و چندبرندی برای مدیریت بازاریابی، محتوا، ارتباط با مشتری و فروش.

## ساختار
- `backend/`: API مبتنی بر ASP.NET Core 10 و PostgreSQL
- `apps/admin-web/`: پنل مدیریتی Next.js و TypeScript
- `docs/`: معماری و وضعیت توسعه

## اجرای محلی
1. `cp .env.example .env`
2. `docker compose up --build`
3. پنل: http://localhost:3000
4. API: http://localhost:5080
5. سلامت API: http://localhost:5080/health

## وضعیت
هسته اولیه API، مدل سازمان و برند، ذخیره‌سازی PostgreSQL و داشبورد فارسی در حال توسعه است. اتصال کانال‌های بیرونی، احراز هویت کامل و عوامل AI تا زمان پیاده‌سازی و آزمون مستقل فعال محسوب نمی‌شوند.
