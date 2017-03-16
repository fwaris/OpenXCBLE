namespace OpenXCBLE
//main code that handles Bluetooth low energy connectivity
open FSM
open Android.Bluetooth
open Android.Bluetooth.LE
open Extensions

type StateHolder = {Gatt:BluetoothGatt; FError:string->unit}
type BLEEvents = Connect of StateHolder | Connected | ServicesDisovered | Disconnect

///Captures device and callbacks that are used when interacting with ble device
type GtState = 
    {
        Device      : BluetoothDevice
        FNotify     : BluetoothGatt->BluetoothGattCharacteristic->unit //ble chrctrstc notificaiton callback
        FConnect    : bool -> unit                                     //connection state callback
        FError      : string -> unit                                   //error occured callback
    }

////register ble characteristic for notification and update the real-time clock
module BLERegisteration =

    //openxc ble ids
    let bleServiceId      = Java.Util.UUID.FromString("6800d38b-423d-4bdb-ba05-c9276d8453e1")
    let bleNotifcationId  = Java.Util.UUID.FromString("6800d38b-5262-11e5-885d-feff819cdce3")
    let bleWriteid        = Java.Util.UUID.FromString("6800d38b-5262-11e5-885d-feff819cdce2")
    let bleNotDescId      = Java.Util.UUID.FromString("00002902-0000-1000-8000-00805f9b34fb")

    //enable notification
    let regNotification s =
        let gatt = s.Gatt
        let srv = gatt.GetService(bleServiceId)
        let cNtfy = srv.GetCharacteristic(bleNotifcationId)
        let b = gatt.SetCharacteristicNotification(cNtfy,true)
        if not b then failwith "unable to enable characteristic notification"
        let dNtfy = cNtfy.GetDescriptor(bleNotDescId)
        let vbytes = BluetoothGattDescriptor.EnableNotificationValue |> Seq.toArray
        let b = dNtfy.SetValue(vbytes)
        if not b then failwith "unable to set descriptor value"
        let b = gatt.WriteDescriptor(dNtfy)
        if not b then failwith "unable to write descriptor"

///Defines the statemachine logic (states and transitions) for interacting with ble device
module BLEStateMachine = 

    //start state
    let rec start = function
        | Connect s  -> if s.Gatt.Connect() then connecting s |> F else errorout s "connecting"
        | Disconnect    -> F start
        | x             -> error (sprintf "invalid event for start %A" x)

    //waiting for connection state
    and connecting s = function
        | Connected     -> if s.Gatt.DiscoverServices() then discoverServices s |> F else errorout s "discover services"
        | Disconnect    -> s.Gatt.Disconnect(); F start
        | x             -> errorout s (sprintf "invalid event for connecting %A" x)

    //waiting for service discovery state
    and discoverServices (s:StateHolder) = function
        | ServicesDisovered -> 
            if s.Gatt.Services = null || s.Gatt.Services.Count = 0 then 
                errorout s "no services"
            else
                try
                    BLERegisteration.regNotification s
                    running s |> F
                with ex ->                    
                    errorout s ex.Message
        | Disconnect    -> s.Gatt.Disconnect(); F start
        | x             -> errorout s (sprintf "invalid event for dscSrvcs %A" x)

    //running state
    and running s = function
        | Disconnect   -> s.Gatt.Disconnect(); F start
        | x             -> errorout s (sprintf "invalid event for running %A" x)

    //error out
    and errorout g s =  try g.Gatt.Disconnect() with _->()
                        try g.FError s; with _ -> ()
                        error s
    and error err    =  logE err; F terminate
    and terminate _  =  logE "terminated"; F terminate


open BLEStateMachine

///Android wrapper class for interacting with ble device
type GattMachine(gts:GtState) as this  =
    inherit BluetoothGattCallback()

    let mutable state = F(start) //connection fsm
    let gatt  = gts.Device.ConnectGatt(ctx,false,this) //bluetoothgatt

    let handleEvent e = state <- evalFSM state e
    let abort msg = gatt.Disconnect(); state <- error msg

    let guardEvent msg g e = 
        logI msg
        if g = GattStatus.Success then 
            handleEvent e 
        else 
            abort ("error: " + msg)

    member x.Connect() = {Gatt=gatt; FError=gts.FError} |> Connect |> handleEvent
    member x.Disconnect() = handleEvent Disconnect

    //android callbacks
    override x.OnCharacteristicChanged(gatt,characteristic) = gts.FNotify gatt characteristic
    override x.OnServicesDiscovered(gatt,s) =  ServicesDisovered |> guardEvent "discover services" s

    override x.OnConnectionStateChange(gatt,s,profile) = 
        logI (sprintf "OnConnectionStateChange %A" profile)
        match profile with
        | ProfileState.Connected        ->  Connected  |> guardEvent "connected" s
                                            gts.FConnect true
        | ProfileState.Disconnected     ->  Disconnect |> guardEvent "disconnected" s
                                            gts.FConnect false
                                            gatt.Close()
                                            gatt.Dispose()
                                            gts.Device.Dispose()
        | ProfileState.Connecting       ->  logI "connecting"
        | ProfileState.Disconnecting    ->  logI "disconnecting"
        | _                             ->  abort "unknown profile state"
            

