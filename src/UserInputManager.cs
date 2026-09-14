using System;
using System.Linq;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.InputSystem.Users;
using UnityEngine.SceneManagement;

public static class UserInputManager
{
    const int MAX_NUM_PLAYERS = 4;
    public class PlayerInputData
    {
        /// <summary>
        /// The actionMap id (corresponding to an entry in the actionMaps Dictionary) that the PlayerInput uses.  If null, uses the existing actionMap and ControlScheme on the player input
        /// </summary>
        public string actionMap;

        public PlayerInputData(string actionMapId)
        {
            actionMap = actionMapId;
        }
    }


    private class UserInputData
    {
        public readonly int id;
        public readonly InputUser user;
        public readonly Dictionary<UserPlayerInput, PlayerInputData> playerInputs;

        /// <summary>
        /// the controlScheme that the PlayerInputs connected to the player will use, if NULL then a default controlscheme will be chosen
        /// </summary>
        public string controlScheme;

        public UserInputData(int id)
        {
            this.id = id;
            user = InputUser.CreateUserWithoutPairedDevices();
            playerInputs = new();
        }
    }

    /// <summary>
    /// InputActionAsset that contains all of the action maps that the manager can keep track of. Loads from a file called "Inputs" in a Resources folder
    /// </summary>
    private static readonly InputActionAsset inputActions = Resources.Load<InputActionAsset>("Inputs");

    /// <summary>
    /// Set of named action maps
    /// </summary>
    private static readonly Dictionary<string, string> actionMaps = new();

    private static readonly UserInputData[] InputData;
    public static int NumPlayers { get; private set; }

    /// <summary>
    /// public event for when a controller is disconnected, provides a parameter corresponding to the playerid whose controller disconnected
    /// </summary>
    public static event Action<int> ControllerDisconnected;

    private static event Action<ConnectInfo> OnConnectInput;

    private static int _listenForConnectInput = 0;

    private static int ListenForConnectInput
    {
        get => _listenForConnectInput;
        set
        {
            if (value < 0)
            {
                throw new InvalidOperationException("ListenForConnectInput cannot be negative");
            }
            if (_listenForConnectInput > value && value == 0)
            {
                InputSystem.onEvent -= OnConnectInputInvoker;
            }
            else if (_listenForConnectInput == 0 && value > 0)
            {
                InputSystem.onEvent += OnConnectInputInvoker;
            }
            _listenForConnectInput = value;
        }
    }

    private static readonly SemaphoreSlim sceneTransitionSemaphore = new(1, 1);

    public static void ClearOnConnectInputListeners()
    {
        OnConnectInput = null;
    }

    private static bool _controllerReconnectorEnabled = true;

    public static bool controllerReconnectorEnabled
    {
        // while in controller connect mode, it is forcibly enabled
        get => _controllerReconnectorEnabled || inControllerConnectMode;
        set => _controllerReconnectorEnabled = value;
    }

    static UserInputManager()
    {
        InputData = new UserInputData[MAX_NUM_PLAYERS];
        for (int i = 0; i < MAX_NUM_PLAYERS; i++)
        {
            InputData[i] = new(i);
        }
        InputUser.onChange += (user, change, device) =>
        {
            DeviceLostHandler(user, change, device);
        };

        ControllerDisconnected += async i =>
        {
            if (controllerReconnectorEnabled)
            {
                Debug.Log($"Player {i} was disconnected");
                UnpairUserInput(i);
                bool startReconnectMethod = playersToReconnect.Count == 0;
                playersToReconnect.Add(i);
                if (startReconnectMethod)
                {
                    await ReconnectController();
                }
            }
        };
    }

    /// <summary>
    /// Binds the the specified actionMap to a specified name
    /// </summary>
    /// <param name="actionMapName">the name that can be used to refer to the actionMap</param>
    /// <param name="actionMap">the actionMap to refer to</param>
    public static void BindActionMap(string actionMapName, string actionMap)
    {
        if (actionMapName is null || actionMap is null)
        {
            throw new NullReferenceException("An argument to SetActionMap is null!");
        }
        if (actionMaps.TryGetValue(actionMapName, out string oldActionMap))
        {
            // check if there is no change to the action map binding
            if (oldActionMap == actionMap)
            {
                return;
            }

            actionMaps[actionMapName] = actionMap;
            foreach (UserInputData inputData in InputData)
            {
                foreach (UserPlayerInput input in inputData.playerInputs.Where(pair => pair.Value.actionMap == actionMapName).Select(pair => pair.Key))
                {
                    PairPlayerInput(input, inputData);
                }
            }
        }
        else
        {
            actionMaps.Add(actionMapName, actionMap);
        }
    }

    public async static Task SwitchScene(string scene)
    {
        await sceneTransitionSemaphore.WaitAsync();
        await SceneManager.LoadSceneAsync(scene);
        sceneTransitionSemaphore.Release();
    }

    private async static Task DeviceLostHandler(InputUser user, InputUserChange change, InputDevice device)
    {
        await sceneTransitionSemaphore.WaitAsync();
        if (change == InputUserChange.DeviceLost)
        {
            if (CheckIfIdInPlayers(user.id) is int id)
            {
                ControllerDisconnected?.Invoke(id);
            }
        }
        sceneTransitionSemaphore.Release();
    }

    private static int? CheckIfIdInPlayers(uint id)
    {
        for (int i = 0; i < NumPlayers; i++)
        {
            if (id == InputData[i].user.id)
            {
                return i;
            }
        }
        return null;
    }

    /// <summary>
    /// Set the control scheme for a player
    /// </summary>
    /// <param name="playerId">the id of the player for whom to set the control scheme</param>
    /// <param name="newControlScheme">the id of the control scheme to set for the player</param>
    /// <exception cref="InvalidOperationException">If the control scheme does not exists in the UserInputManager's InputActions asset</exception>
    public static void SwitchPlayerControlScheme(int playerId, string newControlScheme)
    {
        if (newControlScheme is not null && inputActions.FindControlSchemeIndex(newControlScheme) == -1)
        {
            throw new InvalidOperationException("Control scheme name does not exist in the UserInputManager's InputActions asset");
        }

        IsValidPlayer(playerId);

        // check if there is no change in control scheme
        if (InputData[playerId].controlScheme == newControlScheme)
        {
            return;
        }

        InputData[playerId].controlScheme = newControlScheme;

        PairAllPlayerInputs(playerId);
    }

    /// <summary>
    /// if the specified number is less than current number of players, the excess players are disconnected
    /// </summary>
    /// <param name="num">the new number of players</param>
    /// <exception cref="InvalidOperationException">if the specified number of players is < 0 or > maximum number of players</exception>
    public static void SetNumPlayers(int num)
    {
        if (num < 0 || num > MAX_NUM_PLAYERS)
        {
            throw new InvalidOperationException("Invalid number of players");
        }
        if (num < NumPlayers)
        {
            for (int i = num; i < NumPlayers; i++)
            {
                ClearPlayerInput(i);
                UnpairUserInput(i);
            }
        }
        NumPlayers = num;
    }

    private static void IsValidPlayer(int player)
    {
        if (player < 0 || player >= NumPlayers)
        {
            throw new InvalidOperationException("Invalid player number");
        }
    }

    private static InputUser GetUser(int player)
    {
        return InputData[player].user;
    }

    private static void PairUserInput(int player, InputDevice device, int subdeviceId)
    {
        IsValidPlayer(player);
        InputUser.PerformPairingWithDevice(device, GetUser(player));
        AddSubdevice(device, subdeviceId, player);
    }

    private static void UnpairUserInput(int player)
    {
        RemovePlayerSubdevices(player);

        GetUser(player).UnpairDevices();

        UnpairPlayerInputs(player);
    }

    #region Safe public pairing code

    public static void TryPairDevice(int player, InputDevice device, int subdeviceId)
    {
        if (inControllerConnectMode || IsInControllerReconnectMode())
        {
            throw new InvalidOperationException("In controller connect mode or in controller reconnect mode");
        }

        PairUserInput(player, device, subdeviceId);
        PairAllPlayerInputs(player);
    }

    /// <summary>
    /// unpair a player's input devices, will ask to reconnect a device for the player
    /// </summary>
    /// <param name="player">the player to disconnect devices from</param>
    public static void UnpairPlayerDevices(int player)
    {
        ControllerDisconnected?.Invoke(player);
    }

    #endregion


    #region PlayerInput code


    /// <summary>
    /// Removes 'connection' between the player indicated and the player inputs (unbinds all devices and prevents them from being readded until ConnectPlayerInput is called again, i.e. disconnects all player inputs corresponding to the indicated player)
    /// </summary>
    /// <param name="player">the playerinput number to unbind</param>
    /// <exception cref="InvalidOperationException"></exception>
    public static void ClearPlayerInput(int player)
    {
        List<UserPlayerInput> inputsToRemove = new();
        inputsToRemove.AddRange(GetPlayerInputs(player));
        foreach (UserPlayerInput p in inputsToRemove)
        {
            DisconnectPlayerInput(p, player);
        }
    }

    public static void ClearAllPlayerInputs()
    {
        for (int i = 0; i < InputData.Length; i++)
        {
            ClearPlayerInput(i);
        }
    }

    private static void PairAllPlayerInputs(int player)
    {
        UserInputData user = InputData[player];
        UnpairPlayerInputs(player);
        foreach (UserPlayerInput p in GetPlayerInputs(player))
        {
            PairPlayerInput(p, user);
        }
    }

    /// <summary>
    /// pairs the specified user's controller to the specified playerinput, called after PlayerInputData is already inserted into the playerData
    /// </summary>
    /// <param name="playerInput">the UserPlayerInput to pair</param>
    /// <param name="playerData">the data for the player</param>
    private static void PairPlayerInput(UserPlayerInput playerInput, UserInputData playerData)
    {
        PlayerInputData inputData = playerData.playerInputs[playerInput];
        if (inputData.actionMap is not null)
        {
            playerInput.CloneAndSetInputActionAsset(inputActions);
            // if playerData control scheme is null, choose a default control scheme
            if (playerData.controlScheme is null)
            {
                bool defaultSchemeFound = playerInput.PlayerInput.SwitchCurrentControlScheme(playerData.user.pairedDevices.ToArray());
                if (defaultSchemeFound)
                {
                    Debug.Log($"WARNING: Player {playerData.id} has no ControlScheme, choosing default controlscheme {playerInput.PlayerInput.currentControlScheme}");
                }
                else
                {
                    Debug.Log($"WARNING: Player {playerData.id} has no ControlScheme, unable to find a control scheme");
                }
            }
            else
            {
                playerInput.PlayerInput.SwitchCurrentControlScheme(playerData.controlScheme, playerData.user.pairedDevices.ToArray());
            }
            playerInput.PlayerInput.SwitchCurrentActionMap(actionMaps[inputData.actionMap]);
        }

    }

    public static void ConnectPlayerInput(UserPlayerInput playerInput, int player, PlayerInputData inputData)
    {
        if (playerInput.PlayerId is int playerId)
        {
            throw new InvalidOperationException($"PlayerInput already connected to player {playerId}");
        }
        InputData[player].playerInputs.Add(playerInput, inputData);
        PairPlayerInput(playerInput, InputData[player]);
        playerInput.Connected(player);
    }

    /// <summary>
    /// disconnects a player input from a player
    /// </summary>
    /// <param name="playerInput">the player input to disconnect</param>
    /// <param name="player">the player to disconnect the player input from</param>
    public static void DisconnectPlayerInput(UserPlayerInput playerInput, int player)
    {
        if (playerInput.PlayerInput.user.id != InputUser.InvalidId)
        {
            playerInput.PlayerInput.user.UnpairDevices();
        }
        InputData[player].playerInputs.Remove(playerInput);
        playerInput.Disconnected();
    }

    /// <summary>
    /// resets the devices paired to player inputs corresponding to a player
    /// </summary>
    /// <param name="player">the player id to reset the devices paired to its corresponding player inputs</param>
    private static void UnpairPlayerInputs(int player)
    {
        foreach (UserPlayerInput p in GetPlayerInputs(player))
        {
            if (p?.PlayerInput.user.id != InputUser.InvalidId)
            {
                p?.PlayerInput.user.UnpairDevices();
            }
        }
    }

    private static IEnumerable<UserPlayerInput> GetPlayerInputs(int player)
    {
        return InputData[player].playerInputs.Keys;
    }

    /// <summary>
    /// Unpair every player input on every player
    /// </summary>
    private static void UnpairEveryPlayerInput()
    {
        for (int i = 0; i < MAX_NUM_PLAYERS; i++)
        {
            UnpairPlayerInputs(i);
        }
    }

    /// <summary>
    /// Pair every player input on every player
    /// </summary>
    private static void PairEveryPlayerInput()
    {
        for (int i = 0; i < MAX_NUM_PLAYERS; i++)
        {
            PairAllPlayerInputs(i);
        }
    }

    /// <summary>
    /// switch the action map of a paired UserPlayerInput to a different named actionMap.
    /// </summary>
    /// <param name="playerInput">the UserPlayerInput to switch the action map of</param>
    /// <param name="actionMapName">the name of the actionMap (the bound name).  if NULL, unpairs this UserPlayerInput from updates</param>
    public static void SwitchActionMap(UserPlayerInput playerInput, string actionMapName)
    {
        if (playerInput.PlayerId is int playerId)
        {
            if (!actionMaps.ContainsKey(actionMapName))
            {
                throw new InvalidOperationException("specified action map name is not valid!");
            }

            UserInputData inputData = InputData[playerId];

            inputData.playerInputs[playerInput].actionMap = actionMapName;

            PairPlayerInput(playerInput, inputData);
        }
        else
        {
            throw new InvalidOperationException("playerInput not connected to a player!");
        }
    }

    #endregion

    // events
    /// <summary>
    /// event that is invoked when all players have been connected in ControllerConnectorMode
    /// </summary>
    public static event Action ConnectingFinished;

    /// <summary>
    /// event that is invoked when player is connected, parameters are the player id and a list of tasks that can be added to to delay the next action by the playerconnector, make sure that tasks are added to the list in the syncronous part of the called method
    /// </summary>
    public static event Action<int, List<Task>> PlayerControllerConnected;

    /// <summary>
    /// event that is invoked when player is disconnected, parameters are the player id and a list of tasks that can be added to to delay the next action by the playerconnector, make sure that tasks are added to the list in the syncronous part of the called method
    /// </summary>
    public static event Action<int, List<Task>> PlayerControllerDisconnected;

    /// <summary>
    /// event that is invoked when player is reconnected, parameters are the player id and a list of tasks that can be added to to delay the next action by the playerconnector, make sure that tasks are added to the list in the syncronous part of the called method
    /// </summary>
    public static event Action<int, List<Task>> PlayerControllerReconnected;

    /// <summary>
    /// A method that wraps an async function that responds to a PlayerControllerEvent in a way such that it can be easily added to a PlayerControllerEvent
    /// </summary>
    public static Action<int, List<Task>> PlayerControllerEventWrapper(Func<int, Task> asyncFunctionToCall)
    {
        return (int i, List<Task> lst) =>
        {
            lst.Add(asyncFunctionToCall(i));
        };
    }

    private static async Task InvokePlayerConnectorEvent(Action<int, List<Task>> eventToInvoke, int player)
    {
        List<Task> taskList = new();
        eventToInvoke?.Invoke(player, taskList);
        await Task.WhenAll(taskList);
    }

    // variables for connecting controllers
    private static int currentPlayerToConnect = 0;

    private static readonly List<int> playersToReconnect = new();

    private static readonly SemaphoreSlim reconnectSemaphore = new(1, 1);

    private static readonly SemaphoreSlim connectControllerSemaphore = new(1, 1);

    private static bool inControllerConnectMode = false;



    #region Subdevice code
    // NOTE: a subdevice is a part of a device that is exclusively used by a single player, each subdevice has a unique LOCAL (to parent device) id

    /// <summary>
    /// A struct that represents a the data pertinent to part of an input device that can only belong to one player
    /// </summary>
    private struct InputSubdevice
    {
        /// <summary>
        /// subdevice id local to each parent device.  subids should be fixed for each exclusive "part" of the device
        /// </summary>
        public int id;

        /// <summary>
        /// player the subdevice 'belongs' to
        /// </summary>
        public int playerId;

        public InputSubdevice(int id, int playerId)
        {
            this.id = id;
            this.playerId = playerId;
        }
    }

    /// <summary>
    /// A Dictionary that maps InputDevices to the ids of the subdevices its the parent of. 
    /// </summary>
    private static readonly Dictionary<InputDevice, List<InputSubdevice>> subdeviceMap = new();


    private static bool SubdeviceExists(InputDevice device, int subdeviceId, out int idx)
    {
        idx = -1;
        if (subdeviceMap.TryGetValue(device, out List<InputSubdevice> subdeviceList))
        {
            idx = subdeviceList.FindIndex(subdevice => subdevice.id == subdeviceId);
        }
        return idx != -1;
    }

    private static void AddSubdevice(InputDevice device, int subdeviceId, int player)
    {
        if (SubdeviceExists(device, subdeviceId, out _))
        {
            throw new InvalidOperationException("Subdevice already exists");
        }
        else
        {
            if (subdeviceMap.TryGetValue(device, out List<InputSubdevice> subdeviceList))
            {
                subdeviceList.Add(new(subdeviceId, player));
            }
            else
            {
                subdeviceMap.Add(device, new()
                {
                    new(subdeviceId, player)
                });
            }
        }
    }

    private static void RemovePlayerSubdevices(int player)
    {
        foreach (InputDevice device in GetUser(player).pairedDevices)
        {
            subdeviceMap[device].RemoveAll(subdevice => subdevice.playerId == player);
        }
    }

    #endregion

    public readonly struct ConnectInfo
    {
        public readonly int subid;
        public readonly InputDevice device;
        public readonly string controlScheme;

        public ConnectInfo(int subid, InputDevice device, string controlScheme)
        {
            this.subid = subid;
            this.device = device;
            this.controlScheme = controlScheme;
        }
    }

    /// <summary>
    /// A function that returns a valid string representing the control scheme if the InputAction represents a valid input to connect the controller (ex: a particular button [such as the A button] on a controller) and returns NULL if the InputAction is not a valid connecting input
    /// </summary>
    private static Func<InputControl, ConnectInfo?> validConnectingInputFunction;


    private static void OnConnectInputInvoker(InputEventPtr inputPtr, InputDevice inputDevice)
    {
        foreach (InputControl buttonPress in InputControlExtensions.GetAllButtonPresses(inputPtr))
        {
            if (validConnectingInputFunction(buttonPress) is ConnectInfo connectInfo && !SubdeviceExists(connectInfo.device, connectInfo.subid, out _))
            {
                OnConnectInput.Invoke(connectInfo);
            }
        }
    }

    /// <summary>
    /// starts the controller connector mode where each player must in turn press a certain button on their controller/keyboard to connect to a particular player.  The <c>isValidConnectingInput</c> overrides the previously defined <c>isValidConnectingInput</c> for future controller disconnected prompts
    /// </summary>
    /// <param name="isValidConnectingInput">A function that returns a ConnectInfoStruct if the InputAction represents a valid input to connect the controller (ex: a particular button [such as the A button] on a controller) and returns NULL if the InputAction is not a valid connecting input</param>
    public static void StartControllerConnector(Func<InputControl, ConnectInfo?> isValidConnectingInput)
    {
        // unpair all player inputs
        UnpairEveryPlayerInput();
        // set valid connecting input function
        validConnectingInputFunction = isValidConnectingInput;
        inControllerConnectMode = true;
        ListenForConnectInput++;
        OnConnectInput += ControllerConnectorHandler;
    }

    private static void EndControllerConnector()
    {
        currentPlayerToConnect = 0;

        ListenForConnectInput--;
        PlayerControllerConnected = null;

        ClearOnConnectInputListeners();
        inControllerConnectMode = false;
    }

    private static async Task ReconnectController()
    {
        // wait for reconnect semaphore
        await reconnectSemaphore.WaitAsync();

        // check if still needs to reconnect any controllers (just in case)
        if (playersToReconnect.Count == 0)
        {
            reconnectSemaphore.Release();
            return;
        }

        // wait for connectControllerSemaphore (wait for other controllers to finish connecting)
        await connectControllerSemaphore.WaitAsync();

        // unpair every player input (prevent game from being interacted with while in repairing menu)
        UnpairEveryPlayerInput();

        ListenForConnectInput++;
        ClearOnConnectInputListeners();

        // create new semaphore for
        SemaphoreSlim currentReconnectSemaphore = new(1, 1);
        currentReconnectSemaphore.Wait();


        while (playersToReconnect.Count > 0)
        {
            int player = playersToReconnect[0];
            playersToReconnect.RemoveAt(0);

            Debug.Log($"Reconnect player {player}");
            await InvokePlayerConnectorEvent(PlayerControllerDisconnected, player);

            UserInputManager.OnConnectInput += async c =>
            {
                if (GenericConnectController(c, player))
                {
                    Debug.Log($"Player {player} reconnected");
                    await InvokePlayerConnectorEvent(PlayerControllerReconnected, player);
                    currentReconnectSemaphore.Release();
                    ClearOnConnectInputListeners();
                }
            };
            await currentReconnectSemaphore.WaitAsync();
        }

        currentReconnectSemaphore.Release();
        currentReconnectSemaphore.Dispose();

        // re-pair every player input
        PairEveryPlayerInput();

        ListenForConnectInput--;
        if (inControllerConnectMode)
        {
            OnConnectInput += ControllerConnectorHandler;
        }
        connectControllerSemaphore.Release();
        reconnectSemaphore.Release();
        CheckIfDone();
    }

    private static async void ControllerConnectorHandler(ConnectInfo connectInfo)
    {
        // TODO: hopefully race conditions dont happen here?
        if (!connectControllerSemaphore.Wait(0))
        {
            return;
        }
        if (GenericConnectController(connectInfo, currentPlayerToConnect))
        {
            PairAllPlayerInputs(currentPlayerToConnect);
            await InvokePlayerConnectorEvent(PlayerControllerConnected, currentPlayerToConnect);
            Debug.Log($"Player {currentPlayerToConnect} connected");
            currentPlayerToConnect++;
            CheckIfDone();
        }
        connectControllerSemaphore.Release();
    }

    private static void CheckIfDone()
    {
        if (inControllerConnectMode && currentPlayerToConnect >= NumPlayers)
        {
            Finish();
        }
    }

    /// <summary>
    /// Connects a controller if the input is valid, but DOES NOT pair the player inputs!
    /// </summary>
    /// <param name="inputControl">the inputControl corresponding to the action to check</param>
    /// <param name="playerToConnect">the player to connect the controller to</param>
    /// <returns><c>true</c> if the controller was connected, <c>false</c> otherwise</returns>
    private static bool GenericConnectController(ConnectInfo connectInfo, int playerToConnect)
    {
        if (playerToConnect < NumPlayers)
        {
            InputData[playerToConnect].controlScheme = connectInfo.controlScheme;
            PairUserInput(playerToConnect, connectInfo.device, connectInfo.subid);
            return true;
        }
        return false;
    }

    public static void CancelControllerConnector()
    {
        EndControllerConnector();
        SetNumPlayers(0);
    }

    public static void Finish()
    {
        EndControllerConnector();
        Debug.Log("All players connected");
        ConnectingFinished.Invoke();
        // clear the connecting finish event
        ConnectingFinished = null;
    }

    /// <summary>
    /// is the manager is Controller Reconnect Mode
    /// </summary>
    /// <returns>true if the manager is in controller reconnect mode, false otherwise</returns>
    public static bool IsInControllerReconnectMode()
    {
        return (reconnectSemaphore.CurrentCount > 0 && playersToReconnect.Count > 0) || reconnectSemaphore.CurrentCount == 0;
    }
}