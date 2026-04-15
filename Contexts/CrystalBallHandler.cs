
using System.Text;
using MegaCrit.Sts2.Core.AutoSlay.Helpers;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using NeuroSdk.Actions;
using NeuroSdk.Websocket;
using STS2NeuroIntegration;

namespace Sts2Agent.Contexts;

public class CrystalBallHandler : IContextHandler<CrystalBallHandler.Result>
{
  public class Result : IContextResult
  {
  }
  public ContextType Type => ContextType.CrstalBallEvent;
  public string GetContext(ContextInfo ctx)
  {
    StringBuilder stringBuilder = new();
    stringBuilder.AppendLine("You are at the CrystalBall Event");
    return stringBuilder.ToString();
  }

  public List<ConstructedAction> GetCommands(ContextInfo ctx)
  {
    var commands = new List<ConstructedAction>();
    // var crystalballScreen = ctx.CrystalSphereScreen;
    // if (crystalballScreen == null) return commands;
    // var proceedButton = UiHelper.FindFirst<NProceedButton>(crystalballScreen);
    // if (proceedButton?.IsEnabled == true)
    //   commands.Add(new("proceed", "Proceed out of the Rewards room"));
    commands.Add(new("proceed", "Proceed out of the Rewards room"));

    return commands;
  }

  public ExecutionResult Validate(ConstructedAction action, ActionJData data, Result result, ContextInfo ctx)
  {
    return ExecutionResult.Success();
  }
  public async Task<ExecutionResult?> TryExecute(ConstructedAction action, Result result, ContextInfo ctx)
  {
    return action.Name switch
    {
      _ => null
    };
  }

}
