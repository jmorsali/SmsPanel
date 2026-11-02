# سامانه دریافت شبای پشتیبان

سرویس، درخواست شبا را با فراپیامک ارسال می‌کند و هر پنج دقیقه صندوق ورودی را در SQL Server پردازش می‌کند.

```powershell
cd SmsPanel.Api
dotnet user-secrets init
dotnet user-secrets set "Farapayamak:Username" "..."
dotnet user-secrets set "Farapayamak:Password" "..."
dotnet user-secrets set "Farapayamak:FromNumber" "..."
dotnet ef database update
dotnet run
```

پیام تأیید فقط وقتی ارسال می‌شود که تنظیم عمومی و تنظیم همان کمپین هر دو فعال باشند؛ پیش‌فرض غیرفعال است.

رابط React در پوشه `SmsPanel.Web` قرار دارد، برای موبایل واکنش‌گراست و بعداً می‌تواند به PWA تبدیل شود.
