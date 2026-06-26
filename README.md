# BeeMatrix-Doom

A standalone C# WPF application to run classic shareware **DOOM** on your PC and stream the gameplay in real-time to the **iDotMatrix 32x32 LED Panel** (e.g. `IDM-XXXXXX`) over Bluetooth LE.

<p align="center">
  <img src="logo.png" alt="BeeMatrix Logo" width="150" height="150" />
</p>

## Features

*   **Embedded Wasm DOOM**: Runs the original shareware version of DOOM inside a WPF `WebView2` browser control (hosting [jacobenget.github.io/doom.wasm/examples/browser/doom.html](https://jacobenget.github.io/doom.wasm/examples/browser/doom.html)).
*   **Direct Screen Capture**: Captures the game frame buffer at 20 FPS using GDI+ screen region copies, automatically adjusting for Windows DPI screen scaling.
*   **Low-Latency BLE Stream**: Downscales the frames to `32x32` and streams them via Bluetooth LE using custom chunked packets.
*   **Auto MAC Resolution**: Automatically loads the last connected BLE MAC address from your main BeeMatrix configuration folder.

## Controls

Click inside the game window and use your keyboard directly:
*   **Move**: Arrow keys / WASD
*   **Fire**: Ctrl
*   **Use / Open**: Space
*   **Strafe**: Alt

## Compilation & Run

1.  Compile and publish:
    ```bash
    dotnet build -c Release
    ```
2.  Run the application:
    ```bash
    dotnet run -c Release
    ```
3.  Enter your display's BLE MAC address, click **CONNECT**, and click **START STREAM**.
