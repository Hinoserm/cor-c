# Exception services

`ExceptionDispatchInfo` captures an exception and its trace for later dispatch.
Its public surface follows the .NET API; the runtime supplies the underlying
unwind operation. Bare rethrow and explicit throw remain distinct compiler
operations, including when generic bodies cross an intermediate-object boundary.
