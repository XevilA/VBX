# คู่มือการตั้งค่าฮาร์ดแวร์และการเชื่อมต่อระบบ (Hardware Configuration & I/O Mapping Guide)

เอกสารนี้อธิบายการกำหนดค่าหมายเลข IP (IP Address) และการเชื่อมต่อสัญญาณ I/O สำหรับระบบ **VBX Dispensing Machine Controller**

---

## 1. การตั้งค่า Network (IP Address Configuration)

อุปกรณ์แต่ละชิ้นจะสื่อสารผ่านวงทราฟฟิก LAN โดยใช้ IP เริ่มต้นดังต่อไปนี้ (สามารถเปลี่ยนแปลงได้ผ่านเมนู `CONFIG [F5]` ภายในโปรแกรม หรือแก้ไขในไฟล์ `settings.json`)

| อุปกรณ์ (Device) | หน้าที่ | IP Address (ค่าเริ่มต้น) | Port | โปรโตคอล |
| :--- | :--- | :--- | :--- | :--- |
| **AMSAMOTION MT3A-IO1632** | I/O Controller หลัก | `192.168.1.50` | `502` | Modbus TCP |
| **Cognex In-Sight 2800** | Vision System (กล้อง) | `192.168.1.20` | `23` | TCP / Telnet |
| **Keyence Barcode** | สแกนเนอร์บาร์โค้ด | `192.168.1.10` | `23` | TCP / Telnet |

> **หมายเหตุสำหรับกล้อง:** การแสดงผลภาพสดในโปรแกรมจะทำการดึงภาพผ่าย HTTP จาก URL: `http://192.168.1.20/img/snapshot.jpg` (ตาม IP ที่ตั้งไว้)

---

## 2. การเชื่อมต่อสัญญาณ Input (จากอุปกรณ์เข้าสู่บอร์ด Modbus)

สายสัญญาณ Input ของบอร์ดเชื่อมต่อเพื่อรับสถานะของเซนเซอร์และฮาร์ดแวร์ดังนี้ (อ้างอิงสถานะ `inputs(0)` ถึง `inputs(15)`)

| ตำแหน่ง (DI) | หน้าที่ / ความหมาย | ทำงานระหว่างสถานะ (Mode) |
| :---: | :--- | :--- |
| **DI 00** | **Safety / E-Stop (NC)** ตรวจจับการขัดจังหวะฉุกเฉิน (ต้องมีสัญญาณ 24V) | ทุกสถานะ (Global) |
| **DI 01** | **ปุ่ม Start / รับสัญญาณ Start Cycle** | `READY_WAIT` |
| **DI 02** | **Safety Light Curtain (ม่านแสงนิรภัย)** (NC) | `B1_DISPENSE_POST` |
| **DI 03** | **Cylinder Retract (เซนเซอร์กระบอกสูบหดกลับสุด)** | `A1_WAIT_RETRACT`, `FINISH_SUCCESS` |
| **DI 04** | **Robot Complete (JR3403F - สัญญาณพ่นเสร็จ)** | `B1_DISPENSE_WAIT` |
| **DI 05** | **Robot Fail / Fault (สัญญาณแจ้งเตือนจากหุ่นยนต์)** | `B1_DISPENSE_WAIT` |
| **DI 06** | **Vision OK (สัญญาณ PASS จากกล้อง In-Sight 2800)** | `VISION_CHECK` |
| **DI 07** | **Vision NG (สัญญาณ FAIL จากกล้อง In-Sight 2800)** | `VISION_CHECK` |

*สถานะของเซนเซอร์ Safety และ Interlock (DI 00, 02, 06) เป็นแบบ ปกติปิด (Normally Closed - NC) เพื่อความปลอดภัยสูงสุด หากสัญญาณหายไปจะทริก E-STOP อัตโนมัติ*

---

## 3. การเชื่อมต่อสัญญาณ Output (จากบอร์ด Modbus สั่งงานอุปกรณ์)

สายสัญญาณ Output ของบอร์ดเพื่อควบคุมวาล์ว, หุ่นยนต์ และไฟทาวเวอร์ริ่ง (อ้างอิง `outputs(0)` ถึง `outputs(15)`)

| ตำแหน่ง (DO) | หน้าที่ / ความหมาย | หมายเหตุ |
| :---: | :--- | :--- |
| **DO 00** | **Tower Light - Red (ไฟแดง / แจ้งเตือนหรืออันตราย)** | `ALARM` หรือ `E-Stop Active` |
| **DO 01** | **Tower Light - Yellow (ไฟเหลือง / แจ้งขณะหุ่นยนต์กำลังทำงาน)** | `B1_DISPENSE_START` |
| **DO 02** | **Tower Light - Green (ไฟเขียว / เครื่องอยู่ในสถานะพร้อมทำงาน)** | `A1_REMOVE_PART`, `FINISH_SUCCESS` |
| **DO 03** | **Robot Emergency Signal (ตัดไฟฉุกเฉินหุ่นยนต์)** | ปิดเมื่อปกติ, เปิดเมื่อเกิด E-Stop |
| **DO 04** | **Robot Start (สั่งหุ่นยนต์เริ่มทำงาน)** | สัญญาณ Pulse (500ms) |
| **DO 05** | **Robot Pause (สั่งหุ่นยนต์หยุดชั่วคราว)** | ส่งสัญญาณเมื่องานถูกหยุด |
| **DO 06** | **Program Select - Bit 0 (LSB)** | บิตแรกของการเลือกโปรแกรม |
| **DO 07** | **Program Select - Bit 1** | บิตที่สองของการเลือกโปรแกรม |
| **DO 08** | **Program Select - Bit 2** | บิตที่สามของการเลือกโปรแกรม |
| **DO 09** | **Program Select - Bit 3 (MSB)** | บิตสูงสุดของการเลือกโปรแกรม (รวม 15 โปรแกรม) |
| **DO 10** | **Cylinder Clamp / Unclamp Control** | สั่งโซลินอยด์วาล์วแคลมป์ชิ้นงาน |

### ตารางแปลงการส่งเลข Program (DO 07 ถึง DO 10)
หน้าจอ HMI ควบคุมหุ่นยนต์ได้สูงสุด 15 โปรแกรม โดยแปลงผ่านเลขฐานสอง (Binary):
- **Program 1:** DO 07=ON (เลข 1)
- **Program 2:** DO 08=ON (เลข 2)
- **Program 3:** DO 07=ON, DO 08=ON (เลข 3)
- **Program 15:** ON ทั้ง DO 07, 08, 09, 10 (เลข 15)

---

## 4. รูปแบบคำสั่งการวิชันและสแกนเนอร์ (TCP Handshake)

* **Keyence Barcode Scanner:** เมื่อเครื่องถึงสถานะ `SCAN` โปรแกรมจะส่งข้อความ `LON\r` ผ่าน TCP ไปยังสแกนเนอร์เพื่อเปิดแสงสแกนเนอร์
* **Cognex Vision 2800:** เมื่อเครื่องถึงสถานะ `VISION` โปรแกรมจะส่งข้อความ `T\r` ไปยังกล้องทาง TCP และกล้องต้องตอบกลับเป็นคำที่มี `OK` (เพื่อให้ผ่าน) หากไม่สามารถอ่านจาก TCP ได้ จะใช้สัญญาณ I/O Fallback จากสาย DI 07 เป็นตัวตัดสิน

---
*ผลิตโดย: VBX HMI System Configuration*
