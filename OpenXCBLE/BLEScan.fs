namespace OpenXCBLE
//scans for the openxc bluetooth device using the android api
open Android.App
open Android.Content
open Android.OS
open Android.Runtime
open Android.Views
open Android.Widget
open Android.Bluetooth
open Extensions
open BLERegisteration

type ScanResult = Device of BluetoothDevice | BluetoothNotEnabled | Other of string

module BLEScan =

    type private LeScanCB(fSuccess,fFail) =
        inherit LE.ScanCallback()
        override x.OnScanResult(cbType,scanResult) = fSuccess cbType scanResult
        override x.OnScanFailed(r) = fFail r

    let ble() = ctx.GetSystemService(Context.BluetoothService) :?> BluetoothManager

    let isOpenXC (r:LE.ScanResult) = 
        if  r.ScanRecord <> null  && r.ScanRecord.ServiceUuids <> null then
            let c = r.ScanRecord.ServiceUuids.Count
            if c >= 0 then
                let id = r.ScanRecord.ServiceUuids.[0]
                id.Uuid.Equals(bleServiceId)
            else
                false
        else
            false

    let private scan timeout =
        async {
            let s = ble()
            let e = new System.Threading.ManualResetEvent(false)
            let go() = try e.Set() |> ignore with _ -> ()
            let scanResult =  ref (Other "scan not started")
            let cb = new LeScanCB(
                                    (fun t r -> 
                                        logI (sprintf "device %A" r.Device.Name)
                                        if isOpenXC r then 
                                            scanResult := Device r.Device
                                            logI (sprintf "found device %A" r.Device.Name)
                                            go()
                                    ),
                                    (fun r ->
                                        scanResult := Other  (sprintf "%A" r)
                                        go())
                                    )
            logI "scanning..."
            do s.Adapter.BluetoothLeScanner.StartScan cb
            let! r = Async.AwaitWaitHandle(e,timeout) 
            do e.Dispose() 
            do s.Adapter.BluetoothLeScanner.StopScan(cb)
            logI "scan stopped..."
            return !scanResult
        }

    let scanForOpenXC() = 
        if (ble()).Adapter.IsEnabled |> not then 
            async {return BluetoothNotEnabled}
        else
            scan 30000
