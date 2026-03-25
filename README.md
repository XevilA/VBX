# VBX Dispensing Control System

> Production-grade HMI for automated dispensing with barcode verification, vision inspection, and safety interlocks.

## Architecture

```
┌──────────────┐     Modbus TCP     ┌─────────┐     24V I/O     ┌──────────────┐
│  VBX HMI     │◄──────────────────►│   PLC   │◄──────────────►│  Robot Arm   │
│  (WinForms)  │   192.168.1.12:502 │         │                │  (Dispenser) │
└──────┬───────┘                    └────┬────┘                └──────────────┘
       │                                 │
       │  TCP 9004                       │  24V I/O
       ▼                                 ▼
┌──────────────┐                    ┌──────────────┐
│   Keyence    │                    │  Pneumatic   │
│  Scanner     │                    │  Clamps ×2   │
│ 192.168.1.54 │                    │  (Double Act)│
└──────────────┘                    └──────────────┘
       │
       │  HTTP :80
       ▼
┌──────────────┐
│  Cognex      │
│  In-Sight    │
│  2800        │
│ 192.168.1.20 │
└──────────────┘
```

## Devices

| Device | Protocol | Address | Purpose |
|--------|----------|---------|---------|
| PLC | Modbus TCP | `192.168.1.12:502` | I/O control — 16 DI + 16 DO |
| Keyence Scanner | TCP | `192.168.1.54:9004` | Barcode reading (LON command) |
| Cognex In-Sight 2800 | HTTP + TCP | `192.168.1.20:80` | Vision inspection (listIds API) |
| Robot Controller | Via PLC | — | Dispensing program execution |
| Pneumatic Clamps ×2 | Via PLC | — | Double-acting cylinder, 4 solenoids |
| Tower Light | Via PLC | — | Red / Yellow / Green indicators |
| Light Curtain (Keyence) | Via PLC | I0.3 | Safety — beam break detection |
| E-Stop | Via PLC | I0.0 | Emergency stop (NC circuit) |

---

## I/O Mapping

### Digital Inputs (PLC → Software)

| Index | Signal | Description | Active State |
|-------|--------|-------------|--------------|
| 0 | I0.0 | Emergency Stop | ON = Normal, **OFF = E-Stop** |
| 1 | I0.1 | Start Button 1 | ON = Pressed |
| 2 | I0.2 | Start Button 2 | ON = Pressed |
| 3 | I0.3 | Light Curtain (Keyence) | ON = Normal, **OFF = Interrupted** |
| 4 | I0.4 | Robot Running | ON = Running |
| 5 | I0.5 | Robot Complete (DONE) | ON = Program Finished |
| 6 | I0.6 | Robot Fault | ON = Fault |
| 7 | I0.7 | Vision OK (Cognex) | ON = Pass |
| 8 | I1.0 | Vision NG (Cognex) | ON = Fail |
| 9 | I1.1 | Cylinder Extend Sensor 1 | ON = Unlocked position |
| 10 | I1.2 | Cylinder Retract Sensor 1 | ON = Locked position |
| 11 | I1.3 | Cylinder Extend Sensor 2 | ON = Unlocked position |
| 12 | I1.4 | Cylinder Retract Sensor 2 | ON = Locked position |

### Digital Outputs (Software → PLC)

| Index | Signal | Description | Function |
|-------|--------|-------------|----------|
| 0 | Q0.0 | Tower Light Red | Fault / Safety indicator |
| 1 | Q0.1 | Tower Light Yellow | Running / Standby indicator |
| 2 | Q0.2 | Tower Light Green | Ready / Complete indicator |
| 3 | Q0.3 | *(Reserved)* | — |
| 4 | Q0.4 | Robot Emergency | ON = Stop robot immediately |
| 5 | Q0.5 | Robot Start | 500ms pulse = Start program |
| 6 | Q0.6 | Robot Pause | ON = Pause robot |
| 7 | Q0.7 | Program LOAD | 300ms pulse = Load program bits |
| 8 | Q1.0 | Program Bit 0 | Binary program select (2⁰) |
| 9 | Q1.1 | Program Bit 1 | Binary program select (2¹) |
| 10 | Q1.2 | Program Bit 2 | Binary program select (2²) |
| 11 | Q1.3 | Program Bit 3 | Binary program select (2³) |
| 12 | Q1.4 | Clamp Solenoid 1 (Retract) | ON = Unlock |
| 13 | Q1.5 | Clamp Solenoid 2 (Extend) | ON = **Lock** |
| 14 | Q1.6 | Clamp Solenoid 3 (Retract) | ON = Unlock |
| 15 | Q1.7 | Clamp Solenoid 4 (Extend) | ON = **Lock** |

### Clamp I/O Summary

| Action | Q1.4 | Q1.5 | Q1.6 | Q1.7 |
|--------|------|------|------|------|
| **LOCK** | OFF | **ON** | OFF | **ON** |
| **UNLOCK** | ON | OFF | ON | OFF |

---

## State Machine

```
IDLE ──START──► CLAMP_EXTEND ──Sensors OK──► SCANNING ──Barcode──► MODEL_CHECK
  ▲                                                                    │
  │                                                          Match?────┤
  │                                                     Yes            No
  │                                                      ▼              ▼
  │                                              WAIT_START_CONFIRM  MODEL_FAIL
  │                                                      │              │
  │                                               START──┘          15s/START
  │                                                      ▼              ▼
  │                                              CURTAIN_CHECK   MODEL_FAIL_RETRACT
  │                                                      │              │
  │                                                      ▼              │
  │                                              DISPENSE_START         │
  │                                                      │              │
  │                                                      ▼              │
  │                                              DISPENSE_RUNNING ◄─────┘
  │                                                      │
  │                                               DONE (2s min)
  │                                                      ▼
  │                                              DISPENSE_DONE
  │                                                      │
  │                                                   1.5s delay
  │                                                      ▼
  │                                              VISION_CHECK
  │                                                  │       │
  │                                              PASS        FAIL
  │                                                ▼           ▼
  │                                     VISION_OK_RETRACT  VISION_NG
  │                                                │           │
  │                                         CYCLE_COMPLETE  VISION_NG_RETRACT
  │                                                │           │
  └────────────────────────────────────────────────┘───────────┘
```

### State Details

| State | Lights | Clamp | Camera | Description |
|-------|--------|-------|--------|-------------|
| IDLE | 🟢 Solid | Unlocked | Active | Waiting for START |
| CLAMP_EXTEND | — | Locking | Disabled | Wait sensors I1.2+I1.4 (10s timeout) |
| SCANNING | 🟡 ON | Locked | Disabled | Read barcode via Keyence TCP |
| MODEL_CHECK | — | Locked | Disabled | Match barcode to program map |
| WAIT_START_CONFIRM | 🟢 Blink | Locked | Active | Wait operator 2nd START press |
| CURTAIN_CHECK | — | Locked | Disabled | Log I0.3 status, proceed |
| DISPENSE_START | — | Locked | Disabled | Load program bits + START pulse |
| DISPENSE_RUNNING | 🟡 Blink | Locked | Disabled | Monitor curtain + wait DONE |
| DISPENSE_DONE | 🟢 Blink | Locked | Active | Wait 1.5s for robot retract |
| VISION_CHECK | — | Locked | Active | Trigger Cognex inspection |
| RETRACT states | — | Unlocking | Active | Wait sensors I1.1+I1.3 (10s timeout) |
| CYCLE_COMPLETE | 🟢 Solid | — | Active | Log cycle time → IDLE |
| FAULT_ALARM | 🔴 Solid | — | Active | Q0.4+Q0.6 ON, wait Reset (F2) |
| EMERGENCY_STOP | 🔴 Solid | — | Active | Full stop, START → Home (Program 0) |

---

## Safety Logic

### Emergency Stop (I0.0)
- **Normal:** I0.0 = ON (NC circuit closed)
- **E-Stop:** I0.0 = OFF → immediate `Q0.4 + Q0.6` ON → full robot stop
- **Recovery:** Release E-Stop (I0.0→ON) + press START → **Load Program 0 + START pulse** → Robot returns Home (Set Zero) → IDLE

### Light Curtain (I0.3)
- **Normal:** I0.3 = ON (beam intact)
- **Interrupted:** I0.3 = OFF for ≥200ms → `Q0.4 + Q0.6` ON → robot pause
- **Resume:** Press START → clear Q0.4 + Q0.6 → robot continues
- **Active only during:** `DISPENSE_RUNNING` state
- Clamp remains **LOCKED** during pause

### Clamp Enforcement
- `clampShouldBeLocked` master flag set on START, cleared on RETRACT/IDLE/Reset
- Enforced before **every** Modbus write in `SyncModbusAsync`
- Camera HTTP disabled during robot-critical states to prevent Modbus delays

### Start Button
- **Rising edge** detection — fires once per press, not continuously
- Requires **both** I0.1 AND I0.2 (two-hand safety)

---

## Timing

| Parameter | Value | Purpose |
|-----------|-------|---------|
| Modbus poll | 100ms | Main I/O sync cycle |
| Camera poll | 500ms | Live preview (disabled during robot) |
| START pulse | 500ms | Robot start signal (Q0.5) |
| LOAD pulse | 300ms | Program load signal (Q0.7) |
| DONE filter | 2000ms | Minimum time before accepting I0.5 |
| Curtain debounce | 200ms | Filter electrical noise on I0.3 |
| Clamp lock timeout | 10s | Max wait for I1.2+I1.4 sensors |
| Clamp unlock timeout | 10s | Max wait for I1.1+I1.3 sensors |
| Vision delay | 1500ms | Wait for robot arm to clear camera |
| NG auto-retract | 15s | Auto-unlock if operator doesn't respond |

---

## Robot Program Selection

Programs are selected via 4-bit binary encoding on Q1.0–Q1.3:

| Program | Q1.3 | Q1.2 | Q1.1 | Q1.0 | Decimal |
|---------|------|------|------|------|---------|
| Home/Zero | 0 | 0 | 0 | 0 | 0 |
| Program 1 | 0 | 0 | 0 | 1 | 1 |
| Program 2 | 0 | 0 | 1 | 0 | 2 |
| Program 3 | 0 | 0 | 1 | 1 | 3 |
| ... | ... | ... | ... | ... | ... |
| Program 15 | 1 | 1 | 1 | 1 | 15 |

**Sequence:** Set bits → LOAD pulse (Q0.7, 300ms) → START pulse (Q0.5, 500ms)

---

## Version History

| Version | Changes |
|---------|---------|
| v1.4.9 | Camera disabled during robot states, SE8 trigger removed |
| v1.4.8 | E-Stop Set Zero (Program 0), START edge trigger, clamp master flag |
| v1.4.7 | Clamp unlock timeout fix, camera SE8 trigger |
| v1.4.6 | 2s DONE filter for stale I0.5 signal |
| v1.4.5 | Light curtain I0.3 ON=normal OFF=pause, 200ms debounce |

---

## Build & Release

Automated via GitHub Actions — push a tag to create a Release:

```bash
git tag v1.x.x
git push origin v1.x.x
```

Output: `DispensingControlSystem.exe` (standalone Windows executable)
