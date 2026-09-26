import { ModRegistrar } from "cs2/modding";
import { DetectivePanel, Overlay, ToolbarButton } from "pd/components";

const register: ModRegistrar = (moduleRegistry) => {
  // Toolbar button in the game's top-left button row; panel and overlay in the in-game layer.
  moduleRegistry.append("GameTopLeft", ToolbarButton);
  moduleRegistry.append("Game", DetectivePanel);
  moduleRegistry.append("Game", Overlay);
};

export default register;
