# OpenXCBLE
OpenXC mobile application that uses Bluetooth Low Energy (BLE) interface to the OpenXC C5BLE hardware.

The application records 1) video from the back camera and 2) JSON messages
received from OpenXC. There are two separate files created - one for video and other for data. 
Click the Record button to start video recording and initiation
of Bluetooth connectivity to OpenXC. Upon successful connection to C5BLE, a red dot is shown on the screen.

For each recording session, a set of timestamped files are created in the "C5BLE" folder of the 'external'
storage of the device (which is not likely an external storage card these days).

(Note: this application was tested on Samsung S6 and LG V10 devices both running Android 6.0 or higher)

##Getting Started

This repo is a Visual Studio solution (for version 2017 at the time of writing).
The solution contains one project which is a Xamarin Android application written in
F#. To build this application, download the latest version of Visual Studio and install support for:

- Mobile application development with Xamarin 
- F# language

If you are not familiar with Xamarin development, you might try [this page] (https://www.xamarin.com/visual-studio).

For Xamarin development with F#, here is an older [video by Rachael Reese] (https://www.youtube.com/watch?v=H9uzJFM2Hl)
that might be useful.

##Bluetooth Notes
If you are mainly interested in understanding BLE connectivity to OpenXC, focus on two files:

- BLEScan.fs
- BLEStateMachine.fs

###BLEScan.fs
Contains the code to scan for the OpenXC C5BLE device. It calls the Android API for Bluetooth
scanning and utilizes an implementation of the Android.Bluetooth.LE.ScanCallback interface.

###BLEStateMachine.fs
Contains the code to look for the correct BLE 'characteristic' for receiving data from OpenXC 
and then register for data notifications. The code uses an implementation of the
Android.Bluetooth.BluetoothGattCallback interface. 

The tricky part is waiting for callbacks to complete before making the next API call. 
The API calls have to be done in the right sequence and shoud only be made after the
appropriate callback has completed. Establishing BLE connectivity is an asynchronous process.

The logical sequence is as follows:

Connect --> Discover Services --> Register for notification --> Handle notified data

The function "regNotification" (register for notification) contains the code for
correctly handling the notification registration. Please study this code to understand
how the BLE API works as it is key to enabling the data notifications.

If registration is correct, the data from C5BLE is received on "theOnCharacteristicChanged"
callback method of the impementation class for the
Android.Bluetooth.BluetoothGattCallback interface.







