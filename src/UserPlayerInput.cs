using System;
using UnityEngine;
using UnityEngine.InputSystem;

[DisallowMultipleComponent]
[RequireComponent(typeof(PlayerInput))]
public class UserPlayerInput : MonoBehaviour
{
    public PlayerInput PlayerInput { get; private set; }

    public int? PlayerId { get; private set; } = null;

    /// <summary>
    /// A reference to the last cloned input action asset
    /// </summary>
    private InputActionAsset lastClonedInputActionAsset = null;

    /// <summary>
    /// Clones the passed InputActionAsset and then sets the PlayerInput's asset to hte cloned asset.  Destroys any cloned input action asset that already exists in the PlayerInput
    /// </summary>
    /// <param name="actionAsset">the asset to cloned</param>
    public void CloneAndSetInputActionAsset(InputActionAsset actionAsset)
    {
        DestroyClonedInputActionAsset();
        PlayerInput.actions = Instantiate(actionAsset);
        lastClonedInputActionAsset = PlayerInput.actions;
    }

    /// <summary>
    /// Destroy existing InputActionAsset that is cloned if it exists
    /// </summary>
    private void DestroyClonedInputActionAsset()
    {
        if (lastClonedInputActionAsset is not null && PlayerInput.actions == lastClonedInputActionAsset)
        {
            ScriptableObject.Destroy(PlayerInput.actions);
            lastClonedInputActionAsset = null;
        }
    }

    void Awake()
    {
        PlayerInput = GetComponent<PlayerInput>();
        PlayerInput.neverAutoSwitchControlSchemes = true;
    }

    /// <summary>
    /// Set the state of the object when player input is connected by the UserInputManager to a certain player
    /// </summary>
    /// <param name="playerId">the player the object is connected to</param>
    public void Connected(int playerId)
    {
        PlayerId = playerId;
    }

    /// <summary>
    /// Set the state of the object when player input disconnected by the UserInputManager
    /// </summary>
    public void Disconnected()
    {
        PlayerId = null;
    }

    void OnDestroy()
    {
        if (PlayerId is int playerIdInt)
        {
            UserInputManager.DisconnectPlayerInput(this, playerIdInt);
        }

        DestroyClonedInputActionAsset();
    }
}
