# PolarBLE

**PolarBLE** is a C# .NET 8 WinForms application that connects to a **Polar H10 ECG sensor** over **Bluetooth Low Energy (BLE)** and streams raw ECG data in real-time to **LabStreamingLayer (LSL)** using the [SharpLSL](https://github.com/labstreaminglayer) library.

This project is a port of the original [PolarBand2lsl](https://github.com/markspan/PolarBand2lsl) Python/Kivy implementation.

---

## 🧠 Features

- Scan for nearby **Polar H10** devices via BLE  
- Connect and subscribe to the ECG measurement service  
- Decode and stream **130 Hz** ECG data to **LSL**  
- Simple WinForms UI for quick interaction  

---

## 💻 Requirements

- Windows 10 or 11  
- [.NET 8 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/8.0)  
- Bluetooth LE–capable adapter (internal or external)  
- Polar H10 ECG sensor (Bluetooth-paired or in pairing mode)  

---

## 🚀 Quickstart

### Clone & Restore

```bash
git clone https://github.com/YOUR_USERNAME/PolarBLE.git

 
