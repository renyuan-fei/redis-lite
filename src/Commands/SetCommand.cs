using System.Text;

using codecrafters_redis.Enums;
using codecrafters_redis.Interface;

namespace codecrafters_redis.Commands;

public class SetCommand : IRespCommand
{
  private readonly Dictionary<string, byte[ ]> _workingSet;
  private readonly string                      _name;
  private readonly string                      _value;

  public SetCommand(Dictionary<string, byte[ ]> workingSet, string name, string value)
  {
    _workingSet = workingSet;
    _name = name;
    _value = value;
  }

  public RespResponse Execute()
  {
    var bytes = Encoding.UTF8.GetBytes(_value);

    // add new key and value
    if (_workingSet.TryAdd(_name, bytes))
    {
      return new RespResponse(RespDataType.SimpleString, "OK");
    }

    // update existing key
    _workingSet[_name] = bytes;

    return new RespResponse(RespDataType.SimpleString, "OK");
  }
}