# LINE Jobs API

ASP.NET Core .NET 10, MongoDB Atlas และไลบรารี `../LINEAPI/LINEAPI.csproj`

## ตั้งค่าและ deploy

1. ใน Atlas สร้าง database user ที่มีสิทธิ์ `readWrite` เฉพาะฐานข้อมูลที่จะใช้ เช่น `linejobs` และใช้คลัสเตอร์ที่รองรับ replica-set transactions
2. ใน Render สร้าง **Web Service** จาก GitHub รีโปนี้ เลือก **Docker** ปล่อย **Root Directory** ว่าง และใช้ Dockerfile ที่รากรีโป
3. ในหน้า Service ของ Render เปิด **Connect → Outbound** แล้วเพิ่ม CIDR ของบริการลงใน Atlas **Network Access → IP Access List** ถ้าทดสอบจากเครื่องตัวเอง ให้เพิ่ม IP ของเครื่องนั้นแยกต่างหาก
4. เพิ่มค่าต่อไปนี้ใน **Environment** ของบริการ Render แล้วตั้ง **Health Check Path** เป็น `/healthz`

| ตัวแปร | ค่า |
|---|---|
| `MongoDb__ConnectionString` | Connection string สำหรับ database user จาก Atlas โดยทั่วไปขึ้นต้นด้วย `mongodb+srv://` |
| `MongoDb__Database` | ชื่อฐานข้อมูล ค่าเริ่มต้น `linejobs` |
| `Api__StorageKey` | ค่าสุ่ม 32 ไบต์ในรูป Base64 สำหรับเข้ารหัส payload และโทเคน เก็บค่าเดิมตลอดการ deploy |
| `Api__Clients__<clientId>` | SHA-256 แบบ hex 64 ตัวอักษรของ Bearer key ของผู้ใช้รายนั้น |
| `Line__UserAgent` | User-Agent สำหรับการเชื่อมต่อ LINE |
| `Line__Application` | ค่า X-Line-Application รองรับ `\t` แทน Tab |
| `Line__BaseUrl` | HTTPS origin ของ LINE ใช้ค่าที่กำหนดใน `appsettings.json` ได้ |
| `Line__Locale` | ค่าเริ่มต้น `th_TH` |

Render กำหนด `PORT` ให้ ตัว API ฟังที่ `0.0.0.0` และใช้พอร์ตนั้น ถ้ารัน Docker โดยไม่มี `PORT` จะใช้ `10000` ค่าที่เป็นความลับให้ตั้งใน Environment ของ Render

สำหรับทดสอบด้วย .NET SDK ในเครื่อง ให้กำหนดค่าชุดเดียวกันเป็น environment variables แล้วรันจากรากรีโป

```powershell
dotnet run --project API/API.csproj --urls http://127.0.0.1:5080
```

ใช้ MongoDB replica set ในเครื่องหรือใช้ Atlas ฐานทดสอบแยก ระบบสร้าง collections และ indexes ตอนเริ่มทำงาน `GET /healthz` จะคืน `200` เมื่อ API ติดต่อฐานข้อมูลได้

## เรียก API

ใช้ `Authorization: Bearer <clientKey>` สำหรับทุก route ใต้ `/v1`

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

คำขอสร้างงานตอบ `202` พร้อม `jobId` และ `statusUrl` ระบบรองรับ `Idempotency-Key` สำหรับส่งคำขอเดิมซ้ำหลังเครือข่ายขาด

เมื่อสถานะเป็น `awaitingOtp` ให้แสดงช่องกรอกรหัส แล้วดูสถานะต่อหลังส่งผลสำเร็จ บัญชีที่สมัครสำเร็จคืน `result.accountId` งานเพิ่มเพื่อนคืน `result.friendIds` และงานสร้างกลุ่มคืน `result.groupId`

`409 accountBusy` หมายถึงมีงานที่กำลังใช้บัญชีหรือเบอร์นั้น `410 otpExpired` หมายถึงหมดเวลาส่งรหัส และ `429 queueFull` หมายถึงถึงขีดจำกัดคิว

## คิวและการออกเวอร์ชันใหม่

- จำนวน Worker และขีดจำกัดคิวตั้งได้ผ่าน `Api__Workers`, `Api__QueueCapacity`, `Api__MaxActivePerUser` และ `Api__OtpMinutes`
- งานที่รอ OTP คืนช่อง Worker และหมดอายุตามเวลาที่กำหนด ข้อมูลเซสชันที่ยังไม่หมดอายุอยู่ต่อหลังรีสตาร์ต
- ขณะ deploy มี API/Worker สอง instance ใช้ฐานเดียวกันได้ชั่วคราว การรับงานและล็อกบัญชีอยู่ใน MongoDB
- ถ้า Worker หายไปขณะงานกำลังทำ สถานะจะเป็น `interrupted` หลังสิทธิ์ประมวลผลงานหมดอายุ ให้ตรวจผลบน LINE ก่อนส่งงานใหม่
- รุ่นนี้รองรับสมัครด้วย SMS และสร้างกลุ่มพื้นฐาน ผู้เรียกยังเป็นผู้ส่ง revision ของงานเพิ่มเพื่อนและสร้างกลุ่ม
- การ deploy ชุดนี้สร้างข้อมูลบน MongoDB ฐานใหม่ หากมีข้อมูลทดสอบใน SQLite รุ่นเดิม ให้เก็บไฟล์เดิมไว้สำหรับนำเข้าในงานแยก
