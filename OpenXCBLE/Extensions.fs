//extensions to android API and other utility functions for convenience 
module Extensions
open System
open Android.Widget
open Android.OS

let tag = "opxble" //tag for logging file
let logI d = Android.Util.Log.Info(tag,d)  |> ignore
let logE d = Android.Util.Log.Error(tag,d) |> ignore

let lowercase (s:string) = s.ToLower()
let split cs (s:string) = s.Split(cs |> Seq.toArray)
let join sep (xs:string array) = String.Join(sep,xs)
let startsWith prefix (s:string) = s.StartsWith(prefix,StringComparison.CurrentCultureIgnoreCase)

let ctx = Android.App.Application.Context

let yourself x = x

let toastShort(msg:string) = Toast.MakeText(ctx,msg,ToastLength.Short).Show() |> ignore

module Seq =
    let chunk n xs = seq {
        let i = ref 0
        let arr = ref <| Array.create n (Unchecked.defaultof<'a>)
        for x in xs do
            if !i = n then 
                yield !arr
                arr := Array.create n (Unchecked.defaultof<'a>)
                i := 0 
            (!arr).[!i] <- x
            i := !i + 1
        if !i <> 0 then
            yield (!arr).[0..!i-1] }