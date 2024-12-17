module DeterministicGuid 
open System.Security.Cryptography
open System.Text
open System

let private swapBytes(guid:byte array, left, right) =
    let temp = guid.[left]
    guid.[left] <- guid.[right]
    guid.[right] <- temp

let private swapByteOrder guid =
    swapBytes(guid, 0, 3)
    swapBytes(guid, 1, 2)
    swapBytes(guid, 4, 5)
    swapBytes(guid, 6, 7)

let namespaceBytes = Guid.Parse("3d055908-9d53-469b-b940-824b20c81ed3").ToByteArray()

swapByteOrder namespaceBytes

let create (source:string) =
    let source = Encoding.UTF8.GetBytes source

    let hash =
        use algorithm = SHA1.Create()
        algorithm.TransformBlock(namespaceBytes, 0, namespaceBytes.Length, null, 0) |> ignore
        algorithm.TransformFinalBlock(source, 0, source.Length) |> ignore
        algorithm.Hash

    let newGuid = Array.zeroCreate<byte> 16
    Array.Copy(hash, 0, newGuid, 0, 16)

    newGuid.[6] <- ((newGuid.[6] &&& 0x0Fuy) ||| (5uy <<< 4))
    newGuid.[8] <- ((newGuid.[8] &&& 0x3Fuy) ||| 0x80uy)

    swapByteOrder newGuid
    Guid newGuid
