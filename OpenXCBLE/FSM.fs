namespace OpenXCBLE
//boilerplate for finite-state and mealy machines
//some background: https://www.slideshare.net/FaisalWaris/recognizing-patterns-in-noisy-data-using-trainable-functional-state-machines

type F<'State> = F of ('State -> F<'State>)
type M<'State,'Output> = M of ('State -> M<'State,'Output>)*'Output option

module FSM =
    let evalFSM (F(state)) event = state event
    let evalMealy (M(state,_)) event = state event

