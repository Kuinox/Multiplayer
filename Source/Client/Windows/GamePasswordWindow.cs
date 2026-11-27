using Multiplayer.Common.Networking.Packet;
using UnityEngine;
using Verse;

namespace Multiplayer.Client;

public class GamePasswordWindow : AbstractTextInputWindow
{
    public bool returnToServerBrowser;
    private readonly string username;

    public GamePasswordWindow(string username)
    {
        this.username = username;
        title = "MpGamePassword".Translate();
        doCloseX = false;
        closeOnClickedOutside = false;
        acceptBtnLabel = "MpConnectButton".Translate();
        closeBtnLabel = "CancelButton".Translate();
        passwordField = true;
    }

    public override bool Accept()
    {
        Multiplayer.Client.Send(new ClientUsernamePacket(username, curText));
        Close(false);
        return true;
    }

    public override void OnCancelKeyPressed()
    {
        Event.current.Use();
        OnCloseButton();
    }

    public override void OnCloseButton()
    {
        Multiplayer.StopMultiplayerAndClearAllWindows();

        if (returnToServerBrowser)
            Find.WindowStack.Add(new ServerBrowser());
    }
}
