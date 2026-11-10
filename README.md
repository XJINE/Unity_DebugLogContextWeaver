# Unity_DebugLogContextWeaver

Rewrites UnityEngine.Debug.Log(…)-style calls into the overload that passes 'this' as the context.

## Importing

You can use Package Manager or import it directly.

```
https://github.com/XJINE/Unity_DebugLogContextWeaver.git?path=Assets/Packages/DebugLogContextWeaver
```

### Dependencies

This project use following resources.

- Mono.Cecil:com.unity.nuget.mono-cecil
    - Included via com.unity.multiplayer.tools and com.unity.collections.

## Limitation

- Only instance methods in UnityEngine.Object subclasses.
- Coroutines, async methods, and closures capturing locals are skipped.
    - lambdas and local functions capturing only 'this' are included.
- Static methods and non-UnityEngine.Object classes are ignored, since there is no 'this' to pass