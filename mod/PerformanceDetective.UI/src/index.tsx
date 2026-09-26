import { ModRegistrar } from "cs2/modding";
import { DetectivePanel, Overlay, ToolbarButton } from "pd/components";

const register: ModRegistrar = (moduleRegistry) => {
  // Toolbar button: in the game's top-left button row and in the shared mod menu (with many mods installed the
  // top-left row can run off the screen). The panel also opens with Ctrl+Alt+P or from Options.
  moduleRegistry.append("GameTopLeft", ToolbarButton);
  moduleRegistry.append("UniversalModMenu", ToolbarButton);
  moduleRegistry.append("Game", DetectivePanel);
  moduleRegistry.append("Game", Overlay);
};

export default register;
