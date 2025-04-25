# PolarBLE

**PolarBLE** is a C# .NET 8 WinForms application that connects to a **Polar H10 ECG sensor** over **Bluetooth Low Energy (BLE)** and streams raw ECG data in real-time to **LabStreamingLayer (LSL)**.

This project is a port of my previous [PolarBand2lsl](https://github.com/markspan/PolarBand2lsl) Python/Kivy implementation.
I took info from the project [Dont-hold-your-breath](https://github.com/kieranabrennan/dont-hold-your-breath) by Kieran Brennan. Look it up!

![Screenshot](https://github.com/markspan/PolarBLE/blob/main/Screenshot.png)

---

## 🧠 Features

- Scan for nearby **Polar H10** devices via BLE  
- Connect and subscribe to the ECG measurement service  
- Decode and stream **130 Hz** ECG data to **LSL**  
- Decode and stream **200Hz** ACC data to **LSL** 
---

## 💻 Requirements

- Windows 10 or 11  
- [.NET 8 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/8.0)  
- Bluetooth LE–capable adapter (internal or external)  
- Polar H10 ECG sensor

---

## 🚀 Quickstart

Download the last Release from the GitHub repository, unzip and run.
The Program will start looking for nearby polar bands and display their ID. 
When you click on the ID of the band you want to stream, it will start streaming. 
This can take some time (up to a minute), the programm will inform you when streaming is in progress.
