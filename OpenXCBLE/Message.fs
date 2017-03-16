namespace OpenXCBLE
open Extensions
open System.Text

//state machine for assembling simple, non-nested json messages
//(BLE sends 20 bytes at a time)
//it has two states tick and tock 
//the machines stays in tick state and collects data until a '{' character is seen. then it switches to tock state
//in tock state the machine collects data until '}' is seen. It then parse the json and 
//switches to tick state with any additional data at the end

module Messages =

    let parseJson j = 
        try 
            FsJson.parse j |> Some 
        with ex -> 
            logE (sprintf "invalid json %A" j)
            None

    //the tick state
    let rec tick msgs (i,s:string) =
         if i < s.Length then
            let c = s.[i]
            if c = '{' then
                tock msgs [c] (i+1,s)
            else
                tick msgs (i+1,s)
         else
            M (tick [], Some msgs)
    //tock state
    and tock msgs acc (i,s:string) =
        if i < s.Length then
            let c = s.[i]
            if c = '}' then
                let m = new System.String((c::acc) |> List.rev |> List.toArray) |> parseJson
                tick (m::msgs) (i+1,s)
            else
                tock msgs (c::acc) (i+1,s)
        else
            M (tock [] acc, Some msgs)

