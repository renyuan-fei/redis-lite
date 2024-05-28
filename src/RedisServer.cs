using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

using codecrafters_redis.Enums;
using codecrafters_redis.Interface;
using codecrafters_redis.Service;
using codecrafters_redis.Type;
using codecrafters_redis.Utils;

namespace codecrafters_redis;

public class RedisServer
{
  private readonly ExpiredTasks                          _expiredTask;
  private readonly int                                   _initialTasks;
  private readonly IPAddress                             _ipAddress;
  private readonly string                                _masterHost;
  private readonly int                                   _masterPort;
  private readonly int                                   _masterReplOffset;
  private readonly int                                   _maxTasks;
  private readonly int                                   _port;
  private readonly RedisRole                             _role;
  private readonly ConcurrentDictionary<string, byte[ ]> _simpleStore;
  private          TcpClient                             _tcpClientToMaster;
  private          List<Socket>                          _connectedReplicas = [];

  private RedisServer(
      ExpiredTasks                          expiredTask,
      ConcurrentDictionary<string, byte[ ]> simpleStore,
      RedisRole                             role,
      string                                masterReplid,
      int                                   masterReplOffset,
      int                                   port,
      IPAddress                             ipAddress,
      string                                masterHost,
      int                                   masterPort,
      int                                   initialTasks,
      int                                   maxTasks)
  {
    _port = port;
    _ipAddress = ipAddress;
    _masterPort = masterPort;
    _masterHost = masterHost;
    _expiredTask = expiredTask;
    _simpleStore = simpleStore;
    _role = role;
    MasterReplid = masterReplid;
    _masterReplOffset = masterReplOffset;
    _initialTasks = initialTasks;
    _maxTasks = maxTasks;
  }

  public string MasterReplid { get; }

  public static RedisServer Create(
      ExpiredTasks                          expiredTask,
      ConcurrentDictionary<string, byte[ ]> simpleStore,
      RedisConfig                           config
      )
  {
    return new RedisServer(expiredTask,
                           simpleStore,
                           config.Role,
                           RandomStringGenerator.GenerateRandomString(40),
                           0,
                           config.Port,
                           config.IpAddress,
                           config.MasterHost,
                           config.MasterPort,
                           10,
                           100);
  }

  public async Task StartAsync()
  {
    var server = new TcpListener(_ipAddress, _port);

    // Maximum number of concurrent tasks
    var semaphore = new SemaphoreSlim(_initialTasks, _maxTasks);

    // start server
    try
    {
      server.Start();
      Console.WriteLine($"Redis-lite server is running on port {_port}");

      if (_role == RedisRole.Slave) { await SendCommandsToMaster(); }

      while (true)
      {
        // Wait for a free slot
        await semaphore.WaitAsync();

        // create a new socket instance
        var socket = await server.AcceptSocketAsync();

        // Process the connection in a separate task
        _ = Task.Run(async () =>
        {
          try { await HandleSocket(socket, _expiredTask); }
          finally { semaphore.Release(); }
        });
      }
    }
    catch (Exception ex)
    {
      Console.WriteLine(ex.Message);

      // Release the semaphore if an exception occurs
      semaphore.Release();
    }
    finally { server.Stop(); }
  }

  async private Task HandleSocket(Socket socket, ExpiredTasks expiredTask)
  {
    var buffer = new byte[4096];

    while (socket.Connected)
    {
      // Use the Poll method to check if the connection is still active
      var isConnected = socket.Connected
                     && !(socket.Poll(1, SelectMode.SelectRead)
                       && socket.Available == 0);

      if (!isConnected) { break; }

      // get the data from the socket
      var received = await socket.ReceiveAsync(buffer, SocketFlags.None);

      // Check if any data was received
      if (received == 0) { break; }

      var factory = new RespCommandFactory(buffer, _simpleStore, expiredTask, this);
      var command = factory.Create();
      var response = command.Execute();

      // Encoding the response
      var responseData = Encoding.UTF8.GetBytes(response.GetCliResponse());

      await socket.SendAsync(responseData, SocketFlags.None);

      // If the command implements IPostExecutionCommand, execute the PostExecutionAction
      if (command is IPostExecutionCommand postExecutionCommand)
      {
        postExecutionCommand.PostExecutionAction?.Invoke(socket);

        // After the PSYNC command is executed, add the replica to the list of connected replicas
        _connectedReplicas.Add(socket);
      }
    }

    // close the socket after sending the response
    socket.Close();
  }

  public string GetInfo()
  {
    var info = new Dictionary<string, string>
    {
        { "role", _role.ToString().ToLower() },
        // {"connected_slaves", "0"},
        { "master_replid", MasterReplid },
        { "master_repl_offset", "0" }
        // {"second_repl_offset", "-1"},
        // {"repl_backlog_active", "0"},
        // {"repl_backlog_size", "1048576"},
        // {"repl_backlog_first_byte_offset", "0"},
        // {"repl_backlog_histlen", ""},
    };

    return string.Join('\n', info.Select(x => $"{x.Key}:{x.Value}"));
  }

  async private Task SendCommandsToMaster()
  {
    _tcpClientToMaster = new TcpClient(_masterHost, _masterPort);

    await SendCommandToMaster("*1\r\n$4\r\nping\r\n");

    await SendCommandToMaster($"*3\r\n$8\r\nREPLCONF\r\n$14\r\nlistening-port\r\n$4\r\n{_port}\r\n");

    await SendCommandToMaster("*3\r\n$8\r\nREPLCONF\r\n$4\r\ncapa\r\n$6\r\npsync2\r\n");

    await SendCommandToMaster("*3\r\n$5\r\nPSYNC\r\n$1\r\n?\r\n$2\r\n-1\r\n");
  }

  async private Task SendCommandToMaster(string request)
  {
    var stream = _tcpClientToMaster.GetStream();

    var data = Encoding.ASCII.GetBytes(request);

    await stream.WriteAsync(data);

    var buffer = new byte[1024];
    var bytesRead = await stream.ReadAsync(buffer);
    var response = Encoding.UTF8.GetString(buffer, 0, bytesRead);
    Console.WriteLine(response);
  }

  private void AddReplica(Socket replicaSocket)
  {
    _connectedReplicas.Add(replicaSocket);
  }

  public void PropagateCommandToReplicas(string command)
  {
    byte[] commandData = Encoding.UTF8.GetBytes(command);

    foreach (var replica in _connectedReplicas)
    {
      replica.Send(commandData);
    }
  }
}
