namespace OpenXCBLE
//main activity
open System
open System.IO
open Android.App
open Android.Content
open Android.OS
open Android.Runtime
open Android.Views
open Android.Widget
open Android.Media
open FSM
open Extensions
open Android.Bluetooth
open Android.Bluetooth.LE
open Android.Hardware.Camera2

type UIState = Ready | Starting | BtConnected | Stopping | Playing                            //user interface modes

type RecordFiles = {Writer:System.IO.StreamWriter; VideoPath:string; DataPath:string} with  //structure to hold recording data
                    static member Default = {Writer=null; VideoPath=""; DataPath=""}

[<Activity (
    Label = "OpenXCBLE", 
    MainLauncher = true, Icon = "@drawable/icon", 
    ScreenOrientation = PM.ScreenOrientation.Landscape)>]
type MainActivity () =
    inherit Activity ()
    let uiCtx = Android.App.Application.SynchronizationContext          //holds the UI context for thread synchronization when 
                                                                        //background threads want to update the UI
    
    let mutable uiMode      = Ready                                             //current UI mode
    let mutable gatt        : GattMachine option = None                         //init the variable for the bluetooth object that will provide the notifications
    let mutable cts         = new System.Threading.CancellationTokenSource()    //token to cancel background processing when done
    let mutable agent       : MailboxProcessor<string> = Unchecked.defaultof<_> //agent that asynchronously receives and processes messages
    let mutable recorder    : MediaRecorder = null                              //video recorder
    let mutable canData     = ""                                                //small amount of CAN data to display on the main window
    let mutable recordFiles = RecordFiles.Default                               //initialize recoding location

    //controls
    let mutable btnRecord = null
    let mutable btnPlay   = null
    let mutable btnStop   = null
    let mutable tglHD     = null
    let mutable ctrlVideo = null
    let mutable crcRec    = null
    let mutable txData    = null

    let enable  = List.iter (fun (c:View) -> c.Enabled <- true )                //functions to enable / disable / hide / show UI components
    let disable = List.iter (fun (c:View) -> c.Enabled <- false )
    let visible = List.iter (fun (c:View) -> c.Visibility <- ViewStates.Visible; c.RequestLayout())
    let hide    = List.iter (fun (c:View) -> c.Visibility <- ViewStates.Invisible)

    let doUIWork f =                                                            //run the given function f on the UI thread
        if System.Threading.SynchronizationContext.Current = uiCtx then
            f()                                                                 //just execute it, if call already on UI thread
        else
            async {                                                             //spawn a new background task
                do! Async.SwitchToContext uiCtx                                 //switch to ui thread
                f()                                                             //run the function
            } 
            |> Async.Start

    let saveData (strw:StreamWriter) (j:FsJson.Json) =                          //save can bus message to file
//        let dbs = j |> FsJson.serialize
//        logI dbs
        let name = (j?name).Val
        let dval = (j?value).Val
        let ts   = (j?timestamp).Val
        strw.Write(ts)
        strw.Write('\t')
        strw.Write(name)
        strw.Write('\t')
        strw.Write(dval)
        strw.WriteLine()
        canData <- dval

    let unix_epoch = new System.DateTime(1970,01,01)                         //timestamp conversion 
    let currentTS() = 
        let elapsed = (DateTime.UtcNow - unix_epoch).TotalMilliseconds
        string elapsed

    let genPaths isHD =                                                           //generate new file names for video and data files
        let ts = currentTS().Replace(".","_")
        let p  = Android.OS.Environment.ExternalStorageDirectory.AbsolutePath
        let p  = p + "/C5BLE"
        if Directory.Exists(p) |> not then Directory.CreateDirectory(p) |> ignore
        let pfx = if isHD then "h_" else ""
        let videoPath = sprintf "%s/%sv%s.mp4" p pfx ts
        let dataPath  = sprintf "%s/%sd%s.txt" p pfx ts
        videoPath, dataPath
    
    //add a time marker (as the first record) using the phone's clock
    //it can be used to synchronize data to absolute time
    let addTimeMarker name (strw:StreamWriter) =
        let ts = currentTS()
        strw.Write(ts)
        strw.Write('\t')
        strw.Write(name:string)
        strw.Write('\t')
        strw.WriteLine()

    let addString strw state (s:string) =                               //received a partial string from OpenXC (e.g. a partial json message)
        let (M(_,j)) as nextState = evalMealy state (0,s)               //evaluate a state machine to see if we get a full message (complete json)
        match j with      
        | Some js -> js |> Seq.choose yourself |> Seq.iter (saveData strw)  //if so, save it to file
        | None -> ()
        nextState

    let stopAgent() = cts.Cancel()                                       //stop background processing

    let storeCanDataAgent recordFiles (mb:MailboxProcessor<string>) =    //function that returns an agent for async msg processing
        let rec loop fsm =                                               //main loop of the agent carries the state machine for json processing
            async {
                try 
                    let! s = mb.Receive()                                //we got a message in the mailbox
                    return! loop (addString recordFiles.Writer fsm s)    //check to see if it can be added and then loop with the next state
                with ex -> logE ex.Message
            }
        and start fsm =                                                  //this is the start loop that does one-time processing
            async {                                                      //and then switches to the main loop
                try
                    let! s = mb.Receive()
                    addTimeMarker "first_data" recordFiles.Writer         //add a marker in the data file for time synchronization
                    return! loop (addString recordFiles.Writer fsm s)
                with ex -> logE (sprintf "storeCanDataAgent %s" ex.Message)
            }
        start (M(Messages.tick [],None))                                  //return the agent initilized for start up processing
                                                                          //look at message.fs for how the message assembly state machine works

    let startAgent(tx:TextView) =                                         //turn on the async messaging agent
        stopAgent()
        cts <- new System.Threading.CancellationTokenSource()
        agent <- MailboxProcessor.Start(storeCanDataAgent recordFiles, cts.Token)
        let uiUpdate =                                                     
            async{                                                         //this is a separate async loop that updates the UI periodically
                while true do
                    try 
                        do! Async.Sleep 1000                               //go to sleep for 1 sec 
                        do! Async.SwitchToContext uiCtx
                        tx.Text <- canData
                        do! Async.SwitchToThreadPool()
                    with ex ->
                        logI (sprintf "ui agent %A" ex.Message)
            }
        Async.Start(uiUpdate,cts.Token)

    let getData (c:BluetoothGattCharacteristic) = c.GetStringValue(0)       //extract the string data from bluetooth notification

    let closeFile() =                                                       //close data file being recorded
        if recordFiles.Writer <> null then
            try recordFiles.Writer.Close() with _ -> ()
            recordFiles <- {recordFiles with Writer=null}

    let openFile isHD =                                                     //open data files for recording
        closeFile()
        let videoPath,dataPath = genPaths isHD
        let strm = new System.IO.StreamWriter(dataPath,false)
        recordFiles <- {Writer=strm; VideoPath=videoPath; DataPath=dataPath}

    let stopRecording() =                                                   //stop the video recorder
        try
           if recorder <> null then 
              recorder.Stop()
              recorder.Release()
              recorder <- null
        with ex ->
            recorder <- null
            logI ex.Message

    let startRecording (tglHD:Switch) (ctrlVideo:VideoView) =               //start the video recorder
        try
            ctrlVideo.StopPlayback()
            recorder <- new MediaRecorder()
            recorder.SetVideoSource(VideoSource.Default)
            recorder.SetOutputFormat(OutputFormat.Mpeg4)
            if tglHD.Checked then
                recorder.SetVideoSize(640,480)
            recorder.SetVideoFrameRate(30)
            recorder.SetMaxDuration(30*60*1000)
            recorder.SetVideoEncodingBitRate(3000000/2)
            recorder.SetVideoEncoder(VideoEncoder.Mpeg4Sp)
            recorder.SetOutputFile(recordFiles.VideoPath)
            recorder.SetPreviewDisplay(ctrlVideo.Holder.Surface)
            recorder.Prepare()
            recorder.Start()
        with ex ->
            logI ex.Message

    let disconnect() =                                                        //disconnect from bluetooth
        async {match gatt with Some g -> g.Disconnect() | _ -> ()} |> Async.Start
        gatt <- None

    let connect fUIUpdate =                                                   //connect to bluetooth, fUIUpdate is function to update the UI
        disconnect()
        async {                                                               //async computation that for bluetooth connection
            let! scanResult = BLEScan.scanForOpenXC()                         //scan for device
            match scanResult  with                                            //process scan result
            | BluetoothNotEnabled   -> doUIWork (fun () -> toastShort "bluetooth not enabled")
            | Other e               -> logE e; toastShort "unable to locate openxc device"
            | Device d              -> //found openxc
                let gts =
                    {
                        Device      = d                                               //bluetooth device
                        FNotify     = (fun _ c -> getData c |> agent.Post)            //function executed when we get data (post message to agent)
                        FConnect    = (fun b   -> doUIWork (fun () -> fUIUpdate b))   //function executed when connection is maide
                        FError      = (fun s   -> logI s)                             //function executed when an error is encoutered
                    }
                let g =  new GattMachine(gts)
                gatt <- Some g
                g.Connect()
        }
        |> Async.Start

    let stopAll() =                                                                   //stop everything
        stopAgent()
        stopRecording()
        disconnect()
        closeFile()

    let updateUIState(r) =                                                       //function that updates the UI depending on the UI mode/state
        uiMode <- r
        //logI (match r with Ready->"ready"|Starting->"starting"|Stopping->"stopping"|Recording->"recording"|Playing->"playing")
        doUIWork(fun () ->
        match uiMode with
        | Ready     ->  enable  [btnRecord; btnPlay] 
                        disable [btnStop]
                        hide [crcRec]
        | Starting  
        | Stopping  ->  disable [btnRecord; btnPlay]
                        enable  [btnStop]
                        hide    [crcRec]
        | BtConnected ->disable [btnRecord; btnPlay]
                        enable  [btnStop]
                        visible [crcRec]
                        (txData:TextView).Text <- "..."
        | Playing   ->  disable [btnRecord; btnPlay]
                        enable  [btnStop]
        )

    let updateUIOnConnection (tx:TextView) isConnected =                     //update the UI based on connected or not
        if isConnected then
            updateUIState BtConnected
        else
            updateUIState Ready
            tx.Text <- sprintf "[%s]" tx.Text

    override this.OnCreate (bundle) =                                                //android oncreate method create UI and initialize
        base.OnCreate (bundle)
        this.RequestWindowFeature(WindowFeatures.NoTitle) |> ignore
        this.SetContentView (Resource_Layout.Main)
        btnRecord <- this.FindViewById<Button>(Resource_Id.btnRecord)             //find all UI controls
        btnPlay   <- this.FindViewById<Button>(Resource_Id.btnPlay)
        btnStop   <-this.FindViewById<Button>(Resource_Id.btnStop)
        tglHD     <- this.FindViewById<Switch>(Resource_Id.tglSwitch)
        ctrlVideo <- this.FindViewById<VideoView>(Resource_Id.videoView1)
        crcRec    <- this.FindViewById<View>(Resource_Id.circ)
        txData    <- this.FindViewById<TextView>(Resource_Id.txData)
        
        btnRecord.Click.Add (fun _ ->                                            //event  handler for record button is pressed
            updateUIState Starting
            openFile tglHD.Checked
            startRecording tglHD ctrlVideo
            addTimeMarker "video_start" recordFiles.Writer
            startAgent txData
            connect (updateUIOnConnection  txData)
            )

        btnStop.Click.Add (fun _ ->                                             //event handler for stop button
            match uiMode with
            | Playing ->    updateUIState Stopping
                            ctrlVideo.StopPlayback()
                            updateUIState Ready

            | _      ->     updateUIState Stopping
                            stopAll() 
                            updateUIState Ready
            )

        btnPlay.Click.Add (fun _ ->                                           //event handler for play button
            updateUIState Playing
            ctrlVideo.StopPlayback()
            let uri = Android.Net.Uri.Parse(recordFiles.VideoPath)
            ctrlVideo.SetVideoURI(uri)
            ctrlVideo.Start()
            )

    override this.OnStart() = 
        base.OnStart()
        updateUIState Ready

    override x.OnDestroy() =                                                 //android view exit handler
        base.OnDestroy()
        stopAll()
