namespace GenericSynthesisPatcher.Exceptions
{
    public class GSPActionException : Exception
    {
        public GSPActionException (ProcessingKeys proKeys, string actionName)
            : base(Describe(proKeys, actionName))
        {
            ProcessingKeys = proKeys;
            ActionName = actionName;
        }

        public GSPActionException (ProcessingKeys proKeys, string actionName, string message)
            : base(Describe(proKeys, actionName) + "\n - " + message)
        {
            ProcessingKeys = proKeys;
            ActionName = actionName;
        }

        public GSPActionException (ProcessingKeys proKeys, string actionName, Exception inner)
            : base(Describe(proKeys, actionName), inner)
        {
            ProcessingKeys = proKeys;
            ActionName = actionName;
        }

        public GSPActionException (ProcessingKeys proKeys, string actionName, string message, Exception inner)
            : base(Describe(proKeys, actionName) + "\n - " + message, inner)
        {
            ProcessingKeys = proKeys;
            ActionName = actionName;
        }

        public string ActionName { get; }
        public ProcessingKeys ProcessingKeys { get; }

        private static string Describe (ProcessingKeys keys, string action)
            => $"Error performing rule/group '{keys.RuleBase?.GetLogRuleID() ?? "<unset>"}' action '{action}' on record '{keys.Context.Record.FormKey}'.\n"
                + $" - Config: {keys.RuleBase?.SourceFile ?? "<unknown>"}; field: {keys.Property.PropertyName ?? "<unset>"}; context plugin: {keys.Context.ModKey}\n"
                + $" - Versions: [{Global.Version}|{Global.MutagenVersion}|{Global.SynthesisVersion}]";
    }
}
