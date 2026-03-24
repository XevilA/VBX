# คู่มือการตั้งค่าฮาร์ดแวร์และการเชื่อมต่อระบบ (Hardware Configuration & I/O Mapping Guide)

เอกสารนี้อธิบายการกำหนดค่าหมายเลข IP (IP Address) และการเชื่อมต่อสัญญาณ I/O สำหรับระบบ **VBX Dispensing Machine Controller**

---

## 1. การตั้งค่า Network (IP Address Configuration)

อุปกรณ์แต่ละชิ้นจะสื่อสารผ่าน LAN โดยใช้ IP เริ่มต้นดังต่อไปนี้ (เปลี่ยนแปลงได้ผ่าน `CONFIG [F5]` หรือ `settings.config.json`)

| อุปกรณ์ (Device) | หน้าที่ | IP Address | Port | โปรโตคอล |
| :--- | :--- | :--- | :--- | :--- |
| **ETH-MODBUS-IO16R** | I/O Controller (16DI/16DO) | `192.168.1.12` | `502` | Modbus TCP |
| **Cognex In-Sight 2800** | Vision System (กล้อง) | `192.168.1.20` | `80` | TCP / HTTP |
| **Keyence Barcode Scanner** | สแกนเนอร์บาร์โค้ด | `192.168.1.54` | `23` | TCP / Telnet |

> **หมายเหตุสำหรับกล้อง:** การแสดงผลภาพสดดึงผ่าน HTTP จาก URL: `http://192.168.1.20/img/snapshot.jpg`
> รองรับ FTP (`ftp://ip/path`), TCP (`tcp://ip:port`), และ File Path (`C:\path\image.jpg`)

### อุปกรณ์ไฟฟ้า

| No. | รุ่น | Fuse |
| :---: | :--- | :--- |
| 1 | IC60N/C25A - 2P | 1-F15 |
| 2 | IC60N/C6A - 1P | 7-F12 |
| 3 | ETH-MODBUS-IO16R | 1-B22 |

---

## 2. PC I/O — Safety Enclosure (อุปกรณ์ No.1)

| สัญญาณ | Input | Output | คำอธิบาย |
| :--- | :---: | :---: | :--- |
| Tower Light (3-color R/Y/G) | — | Q0.0-Q0.2 | ไฟสัญญาณ Red/Yellow/Green |
| Emergency Stop Button | I0.0 | — | สวิตช์หยุดฉุกเฉิน (NC) |
| Start Button | I0.1-I0.2 | — | ปุ่มกดเริ่มทำงาน |
| Safety Light Curtain (KEYENCE) | I0.3 | — | ม่านแสงนิรภัย (NC) |

---

## 3. PC I/O — Robot JR3403F (อุปกรณ์ No.2)

| สัญญาณ | Input | Output | Cable Core | คำอธิบาย |
| :--- | :---: | :---: | :---: | :--- |
| Emergency Signal | — | Q0.4 | 3 | สัญญาณฉุกเฉินหุ่นยนต์ |
| Start Signal | — | Q0.5 | 4 | สั่งเริ่มทำงาน (500ms pulse) |
| Pause Signal | — | Q0.6 | 5 | สั่งหยุดชั่วคราว |
| Program Select | — | Q0.7, Q1.0-Q1.3 | 6,7,8,9,13 | เลือกโปรแกรม (binary) |
| Running Signal | I0.4 | — | 10 | หุ่นยนต์กำลังทำงาน |
| Complete Signal | I0.5 | — | 11 | หุ่นยนต์ทำงานเสร็จ |
| Fault Signal | I0.6 | — | 12 | หุ่นยนต์แจ้งเตือน |

### Program Select Encoding (Q0.7 = LOAD, Q1.0-Q1.3 = Binary)

| Output | Coil Index | ความหมาย |
| :---: | :---: | :--- |
| Q0.7 | outputs(7) | Program Number LOAD |
| Q1.0 | outputs(8) | bit0 (2⁰ = 1) |
| Q1.1 | outputs(9) | bit1 (2¹ = 2) |
| Q1.2 | outputs(10) | bit2 (2² = 4) |
| Q1.3 | outputs(11) | bit3 (2³ = 8) |

**ตัวอย่าง:** Program 5 = Q0.7=ON, Q1.0=ON, Q1.2=ON (1+4=5)
**สูงสุด 15 โปรแกรม** (Q1.0-Q1.3 ทั้งหมด ON = 15)

---

## 4. PC I/O — Cognex In-Sight 2800 (อุปกรณ์ No.4)

| สัญญาณ | Input | Output | คำอธิบาย |
| :--- | :---: | :---: | :--- |
| OK | I0.7 | — | สัญญาณ PASS จากกล้อง |
| NG | I1.0 | — | สัญญาณ FAIL จากกล้อง |
| Ethernet | TCP | — | ส่ง `T\r` เพื่อ trigger, ตอบ OK/NG |

---

## 5. PC I/O — KEYENCE Barcode Scanner (อุปกรณ์ No.5)

| สัญญาณ | Port | คำอธิบาย |
| :--- | :---: | :--- |
| Ethernet TCP | 23 | ส่ง `LON\r` เพื่อเปิดสแกน, รับ barcode, `LOFF\r` เพื่อปิด |

---

## 6. Clamp Rock (Cylinder) — Reed Switch (อุปกรณ์ No.6)

| สัญญาณ | Input | Output | คำอธิบาย |
| :--- | :---: | :---: | :--- |
| Cylinder Sensors | I1.1-I1.4 | — | Reed switch ตรวจจับตำแหน่ง |
| Cylinder Clamp Control | — | Q1.4-Q1.7 | โซลินอยด์วาล์วแคลมป์ |

---

## 7. สรุป I/O Mapping (0-indexed Modbus)

### Inputs (`inputs(0)` - `inputs(12)`)

| Index | Modbus | หน้าที่ | NC/NO |
| :---: | :---: | :--- | :---: |
| 0 | I0.0 | **Emergency Stop** | NC |
| 1 | I0.1 | **Start Button** | NO |
| 2 | I0.2 | **Start Button (2)** | NO |
| 3 | I0.3 | **Safety Light Curtain** | NC |
| 4 | I0.4 | **Robot Running** | NO |
| 5 | I0.5 | **Robot Complete** | NO |
| 6 | I0.6 | **Robot Fault** | NO |
| 7 | I0.7 | **Vision OK** (Cognex) | NO |
| 8 | I1.0 | **Vision NG** (Cognex) | NO |
| 9 | I1.1 | **Cylinder Sensor 1** (extend) | NO |
| 10 | I1.2 | **Cylinder Sensor 2** (extend) | NO |
| 11 | I1.3 | **Cylinder Sensor 3** (retract) | NO |
| 12 | I1.4 | **Cylinder Sensor 4** (retract) | NO |

### Outputs (`outputs(0)` - `outputs(15)`)

| Index | Modbus | หน้าที่ |
| :---: | :---: | :--- |
| 0 | Q0.0 | **Tower Light RED** |
| 1 | Q0.1 | **Tower Light YELLOW** |
| 2 | Q0.2 | **Tower Light GREEN** |
| 3 | Q0.3 | *(reserved)* |
| 4 | Q0.4 | **Robot Emergency** |
| 5 | Q0.5 | **Robot Start** (500ms pulse) |
| 6 | Q0.6 | **Robot Pause** |
| 7 | Q0.7 | **Program LOAD** |
| 8 | Q1.0 | **Program bit0** (2⁰=1) |
| 9 | Q1.1 | **Program bit1** (2¹=2) |
| 10 | Q1.2 | **Program bit2** (2²=4) |
| 11 | Q1.3 | **Program bit3** (2³=8) |
| 12 | Q1.4 | **Cylinder Clamp 1** |
| 13 | Q1.5 | **Cylinder Clamp 2** |
| 14 | Q1.6 | **Cylinder Clamp 3** |
| 15 | Q1.7 | **Cylinder Clamp 4** |

---

## 8. Modbus Board Spec

| รายการ | ค่า |
| :--- | :--- |
| Digital I/O | 8 (Note: Recommend 16I/16O) |
| Ethernet Port | 2 |

---
*ผลิตโดย: VBX HMI System — ViscoTec / A Plus Industry*
