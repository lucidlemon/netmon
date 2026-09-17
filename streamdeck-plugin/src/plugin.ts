import streamDeck from "@elgato/streamdeck";
import { ConnectionStatAction } from "./actions/connection-stat";
import { CompositeStatAction } from "./actions/composite-stat";

streamDeck.actions.registerAction(new ConnectionStatAction());
streamDeck.actions.registerAction(new CompositeStatAction());

streamDeck.connect();
