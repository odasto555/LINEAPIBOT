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
| `Auth__BootstrapUsername` | ชื่อแอดมินคนแรก ใช้ตัวอักษรอังกฤษ ตัวเลข `_` หรือ `-` รวม 1–64 ตัว |
| `Auth__BootstrapPassword` | รหัสผ่านแอดมินคนแรก 12–128 ตัวอักษร |
| `Auth__TokenHours` | อายุโทเคน 1–168 ชั่วโมง ค่าเริ่มต้น 12 ชั่วโมง |
| `Line__Version` | เวอร์ชัน LINE ที่เซิร์ฟเวอร์อ่านตอนเริ่มรัน ค่าเริ่มต้น `26.7.1` |
| `Line__UserAgent` | ระบุได้ถ้าต้องการทับค่า `User-Agent` สำรองสำหรับบัญชีที่ยังไม่มีโปรไฟล์และงานสมัครเดิมที่รอ OTP |
| `Line__Application` | ระบุได้ถ้าต้องการทับค่า `X-Line-Application` สำรอง รองรับ `\t` แทน Tab |
| `Line__BaseUrl` | HTTPS origin ของ LINE ใช้ค่าที่กำหนดใน `appsettings.json` ได้ |
| `Line__Locale` | ค่าเริ่มต้น `th_TH` |

API ใช้ค่า `Auth__BootstrapUsername` และ `Auth__BootstrapPassword` เมื่อฐานข้อมูลยังไม่มีผู้ใช้ เมื่อแอดมินล็อกอินได้แล้ว ให้ลบทั้งสองค่าออกจาก Environment ของ Render บัญชีแอดมินอยู่ใน MongoDB

Render กำหนด `PORT` ให้ ตัว API ฟังที่ `0.0.0.0` และใช้พอร์ตนั้น ถ้ารัน Docker โดยไม่มี `PORT` จะใช้ `10000` ค่าที่เป็นความลับให้ตั้งใน Environment ของ Render

สำหรับทดสอบด้วย .NET SDK ในเครื่อง ให้กำหนดค่าชุดเดียวกันเป็น environment variables แล้วรันจากรากรีโป

```powershell
dotnet run --project API/API.csproj --urls http://127.0.0.1:5080
```

ใช้ MongoDB replica set ในเครื่องหรือใช้ Atlas ฐานทดสอบแยก ระบบสร้าง collections และ indexes ตอนเริ่มทำงาน `GET /healthz` จะคืน `200` เมื่อ API ติดต่อฐานข้อมูลได้

## เรียก API

ส่ง `POST /v1/auth/login` พร้อม JSON `username` และ `password` API คืน `accessToken`, `tokenType`, `expiresAt`, `username` และ `role` ใช้ `Authorization: Bearer <accessToken>` กับคำขอที่ต้องยืนยันตัวตน เมื่อตอบ `401` เพราะโทเคนหมดอายุ ให้ล็อกอินใหม่

| Method | Path | JSON / ผลลัพธ์ |
|---|---|---|
| POST | `/v1/auth/login` | `{"username":"member1","password":"…"}` คืนโทเคนเมื่อเข้าสู่ระบบสำเร็จ |
| GET | `/v1/auth/me` | คืนชื่อและสิทธิ์ของผู้ใช้ปัจจุบัน |
| POST | `/v1/auth/logout` | ยกเลิกโทเคนที่ส่งมาและคืน `204` |
| POST | `/v1/admin/users` | แอดมินส่ง `{"username":"member1","password":"…"}` เพื่อสร้างผู้ใช้ทั่วไป คืน `201` |

เมื่อย้ายจาก client key รุ่นเดิม ให้สร้างผู้ใช้ด้วยชื่อที่ตรงกับ `clientId` เดิมทุกตัวอักษร รวมถึงตัวพิมพ์เล็กและใหญ่ เพื่อให้ผู้ใช้เห็นบัญชีและงานที่ผูกกับชื่อนั้นอยู่แล้ว จากนั้นลบค่า `Api__Clients__*` ออกจาก Render และให้โปรแกรมเรียกล็อกอินเพื่อรับโทเคน

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

สำหรับงานสมัครใหม่ เซิร์ฟเวอร์ใช้ `Line__Version` และสุ่มเวอร์ชัน Android จาก `AndroidRandomizer` ชุด `User-Agent` กับ `X-Line-Application` ที่บันทึกในงานจะใช้ต่อจนสมัครเสร็จ รวมถึงหลังรีสตาร์ตระหว่างรอ OTP เมื่อสมัครสำเร็จ ชุดนี้จะบันทึกกับบัญชีแบบเข้ารหัส

เมื่อตั้ง `Line__Version` ใหม่แล้วรีสตาร์ต งานสมัครใหม่และคำขอผูกบัญชี เพิ่มเพื่อน และสร้างกลุ่มที่ใช้ header รูปแบบ Android จะใช้เลขเวอร์ชัน LINE จากการตั้งค่าใหม่ ส่วนเวอร์ชัน Android ใช้ค่าที่บันทึกไว้กับบัญชี หรือ Android 14 ถ้าบัญชียังไม่มีข้อมูลและใช้ค่าสำรองเริ่มต้น

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
