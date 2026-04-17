
using System.Text;
using Godot;
using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.Sts2.Core.AutoSlay.Helpers;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Events;
using MegaCrit.Sts2.Core.Nodes.Events.Custom.CrystalSphere;
using NeuroSdk.Actions;
using NeuroSdk.Json;
using NeuroSdk.Websocket;
using Sts2Agent.Utilities;
using STS2NeuroIntegration;

namespace Sts2Agent.Contexts;

public class CrystalBallHandler : IContextHandler<CrystalBallHandler.Result>
{
  public class Result : IContextResult
  {
    internal NDivinationButton SelectedButton;
    internal NCrystalSphereCell SelectedCell;
    internal NProceedButton ProceedButton;
  }
  public ContextType Type => ContextType.CrstalBallEvent;
  public string GetContext(ContextInfo ctx)
  {
    StringBuilder stringBuilder = new();
    stringBuilder.AppendLine("You are at the CrystalBall Event");
    if (ctx.CrystalSphereScreen == null) return stringBuilder.ToString();
    stringBuilder.AppendLine($"You can reveal cells that might or might not contain a good or bad item. If you fully reveal it you gain the item");

    var instructionstext = ctx.CrystalSphereScreen.Get(NCrystalSphereScreen.PropertyName._instructionsDescriptionLabel).As<MegaRichTextLabel>();
    if (instructionstext != null)
    {
      stringBuilder.AppendLine(TextHelper.StripBBCode(instructionstext.Text));
    }
    var lefttext = ctx.CrystalSphereScreen.Get(NCrystalSphereScreen.PropertyName._divinationsLeftLabel).As<MegaRichTextLabel>();
    if (lefttext != null)
    {
      stringBuilder.AppendLine($"# {TextHelper.StripBBCode(lefttext.Text)}");
    }
    var currentSelection = UiHelper.FindAll<NDivinationButton>(ctx.CrystalSphereScreen).FirstOrDefault(x => x.Get(NDivinationButton.PropertyName._outline).As<Control>().Visible);
    if (currentSelection != null)
    {
      stringBuilder.AppendLine($"# Currently you have {currentSelection.Get(NDivinationButton.PropertyName._label).As<MegaLabel>().Text} selected");
    }
    return stringBuilder.ToString();
  }

  public List<ConstructedAction> GetCommands(ContextInfo ctx)
  {
    var commands = new List<ConstructedAction>();
    if (ctx.CrystalSphereScreen == null) return commands;
    var eventScreen = ctx.CrystalSphereScreen;
    if (UiHelper.FindFirst<NProceedButton>(eventScreen) is NProceedButton proceed && proceed.IsEnabled)
    {
      commands.Add(new("proceed", "Proceed out of the Crystal Ball even"));
      return commands;
    }

    var buttons = UiHelper.FindAll<NDivinationButton>(eventScreen);
    foreach (var divbutton in buttons)
    {
      var label = UiHelper.FindFirst<MegaLabel>(divbutton);
      if (label == null) continue;
      commands.Add(new($"select_{TextHelper.GetActionNameFor(label.Text)}", $"Use to change current Divination Size to {label.Text}"));
    }
    var cells = eventScreen.Get(NCrystalSphereScreen.PropertyName._cellContainer).As<Control>().GetChildren().OfType<NCrystalSphereCell>().ToList();
    if (cells.Count != 0)
    {
      var minX = cells.MinBy(x => x.Entity.X)?.Entity?.X;
      var minY = cells.MinBy(x => x.Entity.Y)?.Entity?.Y;

      var maxX = cells.MaxBy(x => x.Entity.X)?.Entity?.X;
      var maxY = cells.MaxBy(x => x.Entity.Y)?.Entity?.Y;
      commands.Add(new($"reveal_cell", "Reveals a cell at the Selected position between {minX.X},{minY.Y} and {maxX.X},{maxY.Y}, The selected position needs to be inside the sphere", QJS.WrapObject(new Dictionary<string, JsonSchema>()
      {
        ["x"] = new()
        {
          Type = JsonSchemaType.Integer,
          Minimum = minX,
          Maximum = maxX
        },
        ["y"] = new()
        {
          Type = JsonSchemaType.Integer,
          Minimum = minY,
          Maximum = maxY
        },
      })));
    }


    return commands;
  }

  public ExecutionResult Validate(ConstructedAction action, ActionJData data, Result result, ContextInfo ctx)
  {
    if (ctx.CrystalSphereScreen == null) return ExecutionResult.ModFailure("Couldn't find Event screen");
    if (action.Name.StartsWith("select_"))
    {
      var divinationButtons = UiHelper.FindAll<NDivinationButton>(ctx.CrystalSphereScreen);
      foreach (var item in divinationButtons)
      {
        var label = UiHelper.FindFirst<MegaLabel>(item);
        if (label == null) continue;
        if (TextHelper.GetActionNameFor(label.Text) == action.Name.Replace("select_", ""))
        {
          result.SelectedButton = item;
          return ExecutionResult.Success();
        }
      }
      return ExecutionResult.ModFailure("Couldn't find divination button with that name");
    }
    if (action.Name.StartsWith("reveal_"))
    {
      if (data.Data?["x"] == null || data.Data?["y"] == null) return ExecutionResult.Failure("couldn't find cell position parameter");
      var cells = ctx.CrystalSphereScreen.Get(NCrystalSphereScreen.PropertyName._cellContainer).As<Control>().GetChildren().OfType<NCrystalSphereCell>();
      foreach (var cell in cells)
      {
        if ($"{cell.Entity.X}_{cell.Entity.Y}" != $"{data.Data["x"]}_{data.Data["y"]}") continue;
        if (!cell.Entity.IsHidden) return ExecutionResult.Failure($"Position at {data.Data["x"]}x and {data.Data["y"]}y is already unlocked. try a different position");
        result.SelectedCell = cell;
        return ExecutionResult.Success();
      }
      return ExecutionResult.Failure("couldn't find cell with that position, it might not be inside of the Crystal Ball");
    }
    if (action.Name == "proceed")
    {
      if (UiHelper.FindFirst<NProceedButton>(ctx.CrystalSphereScreen) is NProceedButton proceed && proceed.IsEnabled)
      {
        result.ProceedButton = proceed;
        return ExecutionResult.Success();
      }
      else
      {
        return ExecutionResult.ModFailure("Couldn't find proceed button or its not available");
      }

    }
    return ExecutionResult.Unstable("Unkown Action");
  }
  public async Task<ExecutionResult?> TryExecute(ConstructedAction action, Result result, ContextInfo ctx)
  {
    if (action.Name.StartsWith("select_"))
    {
      if (result.SelectedButton == null) return ExecutionResult.Unstable("Passed Validation without Proper Result");
      await GodotMainThread.ClickAsync(result.SelectedButton);
      return ExecutionResult.Success();
    }
    if (action.Name.StartsWith("reveal_"))
    {
      if (result.SelectedCell == null) return ExecutionResult.Unstable("Passed Validation without Proper Result");
      await GodotMainThread.ClickAsync(result.SelectedCell);
      NeuroIntegration.SendContext($"Revealed Cell had {(result.SelectedCell.Entity.Item is null ? "No Item" : $"a part of an item and it was {(result.SelectedCell.Entity.Item.IsGood ? "a Good Item" : "a Bad Item")} be sure to fully reveal it to claim it")}");
      return ExecutionResult.Success();
    }
    if (action.Name == "proceed")
    {
      if (result.ProceedButton == null) return ExecutionResult.Unstable("Passed Validation without Proper Result");
      await GodotMainThread.ClickAsync(result.ProceedButton);
      return ExecutionResult.Success();
    }
    return null;
  }

}
