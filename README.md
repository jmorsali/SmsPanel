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

پیام تأیید فقط وقتی ارسال می‌شود که گزینه آن در همان کمپین فعال باشد؛ پیش‌فرض غیرفعال است.

کمپین جدید ابتدا با وضعیت «آماده ارسال» ذخیره می‌شود. ارسال فقط پس از بررسی اپراتور و فرمان صریح «شروع ارسال» آغاز می‌شود.

رابط React در پوشه `SmsPanel.Web` قرار دارد، برای موبایل واکنش‌گراست و بعداً می‌تواند به PWA تبدیل شود.
