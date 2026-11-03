using System.Text.Json;
using System.Text.Json.Nodes;
using System.IO;

namespace RouteMeter.Windows;
internal static class ProtocolChecks
{
    internal static void Run()
    {
        long now=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var zero=new {download=0UL,upload=0UL};
        var usage=new {direct=zero,proxy=new {download=10UL,upload=2UL},unknown=zero};
        var source=JsonSerializer.Serialize(new {schema=2,program="RouteMeterCore",epoch="fixture",instance="fixture",since=now-1000,timestamp=now,
            sequence=1,totals=usage,apps=new[]{new {id="fixture",name="Example",attribution="executable",usage,active=0,connections=1}},
            uncertainUpload=0,overflow=false,checkpoint=now,measurement="routing-core-payload-lifecycle"});
        void Validate(string json)=>(JsonSerializer.Deserialize<CoreSample>(json,CoreClient.Json)??throw new InvalidDataException()).Validate();
        void Reject(Action action)
        {
            bool rejected=false;try{action();}catch{rejected=true;}
            if(!rejected)throw new InvalidDataException("Invalid protocol fixture was accepted");
        }
        Validate(source);
        var missing=JsonNode.Parse(source)!.AsObject();missing.Remove("totals");Reject(()=>Validate(missing.ToJsonString()));
        var wrong=JsonNode.Parse(source)!;wrong["totals"]!["proxy"]!["download"]=11;Reject(()=>Validate(wrong.ToJsonString()));
        var stale=JsonNode.Parse(source)!;stale["timestamp"]=now-60000;Reject(()=>Validate(stale.ToJsonString()));
        var duplicate=JsonNode.Parse(source)!;duplicate["apps"]!.AsArray().Add(duplicate["apps"]![0]!.DeepClone());Reject(()=>Validate(duplicate.ToJsonString()));
        var secret=new string('a',64);
        _=new Connection{Schema=1,Endpoint="http://127.0.0.1:18765",Token=secret}.Validate();
        Reject(()=>new Connection{Schema=1,Endpoint="http://127.0.0.1.attacker.invalid:18765",Token=secret}.Validate());
        Reject(()=>new Connection{Schema=1,Endpoint="http://user@127.0.0.1:18765",Token=secret}.Validate());
    }
}
