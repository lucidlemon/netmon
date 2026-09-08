import streamDeck from "@elgato/streamdeck";
import { ConnectionStatAction } from "./actions/connection-stat";

streamDeck.actions.registerAction(new ConnectionStatAction());

streamDeck.connect();
