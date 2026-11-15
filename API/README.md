# LINE Jobs API

ASP.NET Core .NET 10. โปรเจกต์อ้างอิง `../LINEAPI/LINEAPI.csproj` โดยตรง

## ตั้งค่าและรัน

กำหนดค่าต่อไปนี้ผ่าน environment variables ของเซิร์ฟเวอร์

| ตัวแปร | ค่า |
|---|---|
| `Api__StorageKey` | ค่าสุ่ม 32 ไบต์ในรูป Base64 สำหรับเข้ารหัสข้อมูลในฐานข้อมูล เก็บค่าเดิมเมื่อรีสตาร์ต |
| `Api__Clients__<clientId>` | SHA-256 แบบ hex 64 ตัวอักษรของ Bearer key ของผู้ใช้แต่ละราย ใช้ค่าสุ่มสำหรับแต่ละราย |
| `Line__UserAgent` | User-Agent สำหรับการเชื่อมต่อ LINE |
| `Line__Application` | ค่า X-Line-Application ใช้ `\t` แทน Tab ได้ |
| `Line__BaseUrl` | HTTPS origin ของ LINE ใช้ค่าจาก appsettings.json ได้ |
| `Line__Locale` | Locale ค่าเริ่มต้น `th_TH` |

`Api__DatabasePath` กำหนดที่เก็บ SQLite ค่าเริ่มต้น `data/api.db` ส่วน `Api__Workers`, `Api__QueueCapacity`, `Api__MaxActivePerUser` และ `Api__OtpMinutes` ใช้ปรับจำนวนงานและอายุ OTP

```powershell
Set-Location 'C:\MainProject\Line\API'
dotnet run --urls http://127.0.0.1:5080
```

เมื่อเปิดให้โปรแกรมบนเครื่องอื่นเรียก ให้ใช้ HTTPS ผ่านโฮสต์หรือ reverse proxy

## สัญญาการเรียก

ส่ง `Authorization: Bearer <clientKey>` ทุกคำขอใต้ `/v1` ตัวโปรแกรมเก็บ Bearer key ของผู้ใช้รายนั้นได้ ส่วน storage key ใช้เฉพาะบนเซิร์ฟเวอร์

| Method | Path | JSON / ผลลัพธ์ |
|---|---|---|
| POST | `/v1/jobs/registration` | `phoneNumber` แบบ E.164, `countryCode`, `displayName`, `password`, `deviceId`, `deviceModel` |
| POST | `/v1/jobs/add-friends` | `accountId`, `phoneNumbers`, `revision`, `sequenceId` (ไม่บังคับ) |
| POST | `/v1/jobs/create-group` | `accountId`, `name`, `userIds`, `revision` |
| GET | `/v1/jobs/{jobId}` | สถานะ ขั้นตอน กำหนดส่ง OTP และผลลัพธ์ของงาน |
| GET | `/v1/jobs?limit=25` | งานล่าสุดของผู้ใช้ |
| POST | `/v1/jobs/{jobId}/otp` | `{"code":"012345"}` ใช้ string เพื่อรักษาเลขศูนย์ด้านหน้า |
| POST | `/v1/accounts` | `{"accessToken":"…"}` ผูกบัญชีเดิม โดยอ่าน Account ID จาก LINE |
| GET | `/v1/accounts` | รายการ Account ID ที่ผู้ใช้สั่งงานได้ |
| GET | `/healthz` | สถานะตัว API |

คำขอสร้างงานตอบ `202` พร้อม `jobId` และ `statusUrl` ระบบรองรับ `Idempotency-Key` ในคำขอสร้างงาน ใช้คีย์เดิมเมื่อต้องส่งคำขอเดิมซ้ำหลังเครือข่ายขาด

เมื่อสถานะเป็น `awaitingOtp` ให้แสดงช่องกรอกรหัส ถ้าส่งทันและได้รับ `202` ให้ดูสถานะต่อ ผลสมัครสำเร็จคืน `result.accountId` เพื่อใช้กับงานเพิ่มเพื่อนหรือสร้างกลุ่ม ผลสร้างกลุ่มคืน `result.groupId` งานเพิ่มเพื่อนคืน `result.friendIds`

`409 accountBusy` หมายถึงมีงานที่ใช้งานบัญชีหรือเบอร์สมัครอยู่แล้ว `410 otpExpired` หมายถึงหมดเวลาส่งรหัส `429 queueFull` หมายถึงถึงขีดจำกัดคิว ถ้า API หยุดระหว่างทำงาน สถานะ `interrupted` ให้ตรวจผลกับ LINE ก่อนส่งงานใหม่

รุ่นนี้ใช้ API instance เดียวต่อไฟล์ SQLite และมี Worker หลายตัวใน instance เดียว การรอ OTP ไม่ใช้ช่อง Worker รุ่นนี้รองรับการสมัครด้วย SMS และสร้างกลุ่มพื้นฐาน Caller เป็นผู้ส่ง revision ของคำขอเพิ่มเพื่อนและสร้างกลุ่ม
