using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using DMCompiler;
using DMCompiler.Bytecode;
using DMCompiler.Compiler;
using JetBrains.Annotations;
using OpenDreamRuntime.Objects;
using OpenDreamRuntime.Objects.Types;
using OpenDreamRuntime.Procs.Native;
using OpenDreamRuntime.Rendering;
using OpenDreamRuntime.Resources;
using OpenDreamShared.Dream;
using Robust.Shared.Random;
using FormatSuffix = DMCompiler.Bytecode.StringFormatEncoder.FormatSuffix;
using BlendType = OpenDreamRuntime.Objects.DreamIconOperationBlend.BlendType;

namespace OpenDreamRuntime.Procs;

internal static partial class DMOpcodeHandlers {
    #region Values

    public static ProcStatus PushReferenceValue(DMProcState state) {
        DreamReference reference = state.ReadReference();
        using DreamValue value = state.GetReferenceValue(reference);

        state.Push(value);
        return ProcStatus.Continue;
    }

    public static ProcStatus Assign(DMProcState state) {
        DreamReference reference = state.ReadReference();
        using DreamValue value = state.Pop();

        state.AssignReference(reference, value);
        state.Push(value);
        return ProcStatus.Continue;
    }

    public static ProcStatus AssignInto(DMProcState state) {
        DreamReference reference = state.ReadReference();
        using DreamValue value = state.Pop();
        using DreamValue first = state.GetReferenceValue(reference);

        //TODO call operator:= for DreamObjects
        state.AssignReference(reference, value);
        state.Push(value);

        return ProcStatus.Continue;
    }

    public static ProcStatus CreateList(DMProcState state) {
        int size = state.ReadInt();
        DreamList list = state.Proc.ObjectTree.CreateList(size);

        foreach (DreamValue value in state.PopCount(size)) {
            list.AddValue(value);
            value.Dispose();
        }

        state.Push(new DreamValue(list));
        list.DecRef();
        return ProcStatus.Continue;
    }

    public static ProcStatus CreateMultidimensionalList(DMProcState state) {
        int dimensionCount = state.ReadInt();
        ReadOnlySpan<DreamValue> dimensionSizes = state.PopCount(dimensionCount);

        DreamList list = state.Proc.ObjectTree.CreateList();

        // Same as new /list(1, 2, 3)
        using (var listInitArgs =
               new DreamProcArguments(dimensionSizes)) // Needs disposed of before we modify the stack again with Push()
        {
            list.Initialize(listInitArgs);
        }

        foreach (DreamValue value in dimensionSizes)
            value.Dispose();

        state.Push(new DreamValue(list));
        list.DecRef();
        return ProcStatus.Continue;
    }

    public static ProcStatus CreateAssociativeList(DMProcState state) {
        int size = state.ReadInt();
        DreamList list = state.Proc.ObjectTree.CreateList(size);
        ReadOnlySpan<DreamValue> popped = state.PopCount(size * 2);

        for (var i = 0; i < popped.Length; i += 2) {
            using DreamValue key = popped[i];
            using DreamValue value = popped[i + 1];

            if (key.IsNull)
                list.AddValue(value);
            else
                list.SetValue(key, value, true);
        }

        state.Push(new DreamValue(list));
        list.DecRef();
        return ProcStatus.Continue;
    }

    public static ProcStatus CreateStrictAssociativeList(DMProcState state) {
        int size = state.ReadInt();
        DreamAssocList list = state.Proc.ObjectTree.CreateAssocList(size);
        ReadOnlySpan<DreamValue> popped = state.PopCount(size * 2);

        for (var i = 0; i < popped.Length; i += 2) {
            using DreamValue key = popped[i];
            using DreamValue value = popped[i + 1];

            list.SetValue(key, value, true);
        }

        state.Push(new DreamValue(list));
        list.DecRef();
        return ProcStatus.Continue;
    }

    private static IDreamValueEnumerator GetContentsEnumerator(AtomManager atomManager, DreamValue value,
        TreeEntry? filterType) {
        if (!value.TryGetValueAsIDreamList(out IDreamList? list))
            if (value.TryGetValueAsDreamObject(out DreamObject? dreamObject)) {
                switch (dreamObject)
                {
                    case null:
                        return new DreamValueArrayEnumerator([], null);
                    case DreamObjectAtom:
                    {
                        using DreamValue contents = dreamObject.GetVariable("contents");

                        list = contents.MustGetValueAsDreamList();
                        break;
                    }
                    case DreamObjectWorld:
                        return new WorldContentsEnumerator(atomManager, filterType);
                }
            }

        if (list != null) {
            // world.contents has its own special enumerator to prevent the huge copy
            if (list is WorldContentsList)
                return new WorldContentsEnumerator(atomManager, filterType);

            DreamValue[] values = list.CopyToArray();
            Dictionary<DreamValue, DreamValue>? assocValues = list.IsAssociative ? list.CopyAssocValues() : null;
            foreach (DreamValue copiedValue in values)
                copiedValue.IncRef();
            if (assocValues != null)
                foreach (DreamValue copiedAssocValue in assocValues.Values)
                    copiedAssocValue.IncRef();

            return filterType == null
                ? new DreamValueArrayEnumerator(values, assocValues)
                : new FilteredDreamValueArrayEnumerator(values, assocValues, filterType);
        }

        // BYOND ignores all floats, strings, types, etc. here and just doesn't run the loop.
        return new DreamValueArrayEnumerator([], null);
    }

    public static ProcStatus CreateListEnumerator(DMProcState state) {
        int enumeratorId = state.ReadInt();
        using DreamValue list = state.Pop();
        IDreamValueEnumerator enumerator = GetContentsEnumerator(state.Proc.AtomManager, list, null);

        state.Enumerators[enumeratorId] = enumerator;
        return ProcStatus.Continue;
    }

    public static ProcStatus CreateFilteredListEnumerator(DMProcState state) {
        int enumeratorId = state.ReadInt();
        int filterTypeId = state.ReadInt();
        TreeEntry filterType = state.Proc.ObjectTree.GetTreeEntry(filterTypeId);
        using DreamValue list = state.Pop();
        IDreamValueEnumerator enumerator = GetContentsEnumerator(state.Proc.AtomManager, list, filterType);

        state.Enumerators[enumeratorId] = enumerator;
        return ProcStatus.Continue;
    }

    public static ProcStatus CreateTypeEnumerator(DMProcState state) {
        int enumeratorId = state.ReadInt();
        using DreamValue typeValue = state.Pop();
        if (!typeValue.TryGetValueAsType(out TreeEntry? type))
            throw new DMException($"Cannot create a type enumerator with type {typeValue}");

        if (type == state.Proc.ObjectTree.Client) {
            state.Enumerators[enumeratorId] = new DreamObjectEnumerator(state.DreamManager.Clients);
            return ProcStatus.Continue;
        }

        if (type.ObjectDefinition.IsSubtypeOf(state.Proc.ObjectTree.Atom)) {
            state.Enumerators[enumeratorId] = new WorldContentsEnumerator(state.Proc.AtomManager, type);
            return ProcStatus.Continue;
        }

        if (type.ObjectDefinition.IsSubtypeOf(state.Proc.ObjectTree.Datum)) {
            IEnumerable<DreamObject> datumEnumerator = state.Proc.RefManager.EnumerateType(RefType.DreamObjectDatum);

            state.Enumerators[enumeratorId] = new DreamObjectEnumerator(datumEnumerator, type);
            return ProcStatus.Continue;
        }

        throw new DMException($"Type enumeration of {type} is not supported");
    }

    public static ProcStatus CreateRangeEnumerator(DMProcState state) {
        int enumeratorId = state.ReadInt();
        using DreamValue step = state.Pop();
        using DreamValue rangeEnd = state.Pop();
        using DreamValue rangeStart = state.Pop();

        if (!step.TryGetValueAsFloat(out float stepValue))
            throw new DMException($"Invalid step {step}, must be a number");
        if (!rangeEnd.TryGetValueAsFloat(out float rangeEndValue))
            throw new DMException($"Invalid end {rangeEnd}, must be a number");
        if (!rangeStart.TryGetValueAsFloat(out float rangeStartValue))
            throw new DMException($"Invalid start {rangeStart}, must be a number");

        state.Enumerators[enumeratorId] = new DreamValueRangeEnumerator(rangeStartValue, rangeEndValue, stepValue);
        return ProcStatus.Continue;
    }

    public static ProcStatus CreateObject(DMProcState state) {
        DMProcState.DMStackArgumentInfo argumentInfo = state.ReadProcArguments();
        using DreamValue val = state.Pop();
        using DreamValue overridesVal = state.Pop();
        Dictionary<string, object?>? overrides = null;
        if (overridesVal.TryGetValueAsString(out string? jsonDict))
            overrides = JsonSerializer.Deserialize<Dictionary<string, object?>>(jsonDict);

        if (!val.TryGetValueAsType(out TreeEntry? objectType)) {
            if (val.TryGetValueAsString(out string? pathString)) {
                if (!state.Proc.ObjectTree.TryGetTreeEntry(pathString, out objectType))
                    ThrowCannotCreateUnknownObject(val);
            } else if (val.TryGetValueAsProc(out DreamProc? proc)) {
                // new /proc/proc_name(Destination,Name,Desc)
                using DreamProcArguments arguments = state.PopProcArguments(null, argumentInfo);
                DreamValue destination = arguments.GetArgument(0);

                // TODO: Name and Desc arguments

                if (destination.TryGetValueAsDreamObject<DreamObjectAtom>(out DreamObjectAtom? atom))
                    state.Proc.AtomManager.UpdateAppearance(atom, appearance => {
                        state.Proc.VerbSystem.RegisterVerb(proc);

                        appearance.Verbs.Add(proc.VerbId!.Value);
                    });
                else if (destination.TryGetValueAsDreamObject<DreamObjectClient>(out DreamObjectClient? client))
                    client.ClientVerbs.AddValue(val);

                return ProcStatus.Continue;
            } else {
                ThrowCannotCreateObjectFromInvalid(val);
            }
        }

        DreamObjectDefinition objectDef = objectType.ObjectDefinition;
        DreamProc newProc = objectDef.GetProc("New");
        DreamProcArguments newArguments = state.PopProcArguments(newProc, argumentInfo);

        if (objectDef.IsSubtypeOf(state.Proc.ObjectTree.Turf)) {
            // Turfs are special. They're never created outside of map initialization
            // So instead this will replace an existing turf's type and return that same turf
            DreamValue loc = newArguments.GetArgument(0);
            if (!loc.TryGetValueAsDreamObject<DreamObjectTurf>(out DreamObjectTurf? turf))
                ThrowInvalidTurfLoc(loc);

            state.Proc.DreamMapManager.SetTurf(turf, objectDef, newArguments);
            if (overrides is not null)
                foreach (KeyValuePair<string, object?> varOverride in overrides) {
                    using DreamValue overrideValue =
                        state.Proc.ObjectTree.GetDreamValueFromJsonElement(varOverride.Value);

                    turf.SetVariable(varOverride.Key, overrideValue);
                }

            state.Push(loc);
            return ProcStatus.Continue;
        }

        DreamObject newObject = state.Proc.ObjectTree.CreateObject(objectType);
        if (overrides is not null)
            foreach (KeyValuePair<string, object?> varOverride in overrides) {
                using DreamValue overrideValue = state.Proc.ObjectTree.GetDreamValueFromJsonElement(varOverride.Value);

                newObject.SetVariable(varOverride.Key, overrideValue);
            }

        ProcState s = newObject.InitProc(state.Thread, state.Usr, newArguments);

        state.Thread.PushProcState(s);
        newObject.DecRef();
        return ProcStatus.Called;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowInvalidTurfLoc(DreamValue loc) {
        throw new DMException($"Invalid turf loc {loc}");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowCannotCreateObjectFromInvalid(DreamValue val) {
        throw new DMException($"Cannot create object from invalid type {val}");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowCannotCreateUnknownObject(DreamValue val) {
        throw new DMException($"Cannot create unknown object {val}");
    }

    public static ProcStatus DestroyEnumerator(DMProcState state) {
        int enumeratorId = state.ReadInt();

        state.Enumerators[enumeratorId]?.Dispose();
        state.Enumerators[enumeratorId] = null;
        return ProcStatus.Continue;
    }

    public static ProcStatus Enumerate(DMProcState state) {
        int enumeratorId = state.ReadInt();
        DreamReference outputRef = state.ReadReference();
        int jumpToIfFailure = state.ReadInt();

        IDreamValueEnumerator? enumerator = state.Enumerators[enumeratorId];
        if (enumerator == null || !enumerator.Enumerate(state, outputRef, DreamReference.NoRef))
            state.Jump(jumpToIfFailure);

        return ProcStatus.Continue;
    }

    public static ProcStatus EnumerateAssoc(DMProcState state) {
        int enumeratorId = state.ReadInt();
        DreamReference assocRef = state.ReadReference();
        DreamReference outputRef = state.ReadReference();
        int jumpToIfFailure = state.ReadInt();

        IDreamValueEnumerator? enumerator = state.Enumerators[enumeratorId];
        if (enumerator == null || !enumerator.Enumerate(state, outputRef, assocRef))
            state.Jump(jumpToIfFailure);

        return ProcStatus.Continue;
    }

    public static ProcStatus EnumerateNoAssign(DMProcState state) {
        int enumeratorId = state.ReadInt();
        IDreamValueEnumerator? enumerator = state.Enumerators[enumeratorId];
        int jumpToIfFailure = state.ReadInt();

        if (enumerator == null || !enumerator.Enumerate(state, DreamReference.NoRef, DreamReference.NoRef))
            state.Jump(jumpToIfFailure);

        return ProcStatus.Continue;
    }

    /// <summary>
    ///     Helper function of <see cref="FormatString" /> to handle text macros that are "suffix" (coming after the noun)
    ///     pronouns
    /// </summary>
    /// <param name="formattedString"></param>
    /// <param name="interps"></param>
    /// <param name="prevInterpIndex"></param>
    /// <param name="pronouns">This should be in MALE,FEMALE,PLURAL,NEUTER order.</param>
    private static void HandleSuffixPronoun(ref StringBuilder formattedString, ReadOnlySpan<DreamValue> interps,
        int prevInterpIndex, string[] pronouns) {
        if (prevInterpIndex == -1 || prevInterpIndex >= interps.Length) // We should probably be throwing here
            return;
        if (!interps[prevInterpIndex].TryGetValueAsDreamObject<DreamObject>(out DreamObject? dreamObject))
            return;
        if (!dreamObject.TryGetVariable("gender",
                out DreamValue objectGender)) // NOTE: in DM, this has to be a native property.
            return;
        if (!objectGender.TryGetValueAsString(out string? genderStr))
            return;

        switch (genderStr) {
            case "male":
                formattedString.Append(pronouns[0]);
                return;
            case "female":
                formattedString.Append(pronouns[1]);
                return;
            case "plural":
                formattedString.Append(pronouns[2]);
                return;
            case "neuter":
                formattedString.Append(pronouns[3]);
                return;
            default:
                return;
        }
    }

    private static void ToRoman(ref StringBuilder formattedString, ReadOnlySpan<DreamValue> interps,
        int nextInterpIndex, bool upperCase) {
        char[] arr;
        if (upperCase)
            arr = new[] {'M', 'D', 'C', 'L', 'X', 'V', 'I'};
        else
            arr = new[] {'m', 'd', 'c', 'l', 'x', 'v', 'i'};

        var numArr = new[] {1000, 500, 100, 50, 10, 5, 1};

        if (!interps[nextInterpIndex].TryGetValueAsFloat(out float value)) return;

        switch (value)
        {
            case Single.NaN:
                formattedString.Append('-'); //BYOND prints - for this
                return;
            case < 0:
                formattedString.Append('-');
                value = MathF.Abs(value);
                break;
        }

        if (float.IsInfinity(value)) {
            formattedString.Append("inf");
            return;
        }

        var intValue = (int)value;
        var i = 0;

        while (intValue != 0)
            if (intValue >= numArr[i]) {
                intValue -= numArr[i];
                formattedString.Append(arr[i]);
            } else {
                i++;
            }
    }

    public static ProcStatus FormatString(DMProcState state) {
        string unformattedString = state.ReadString();
        var formattedString = new StringBuilder();

        int interpCount = state.ReadInt();

        FormatSuffix? postPrefix = null; // Prefix that needs the effects of a suffix

        ReadOnlySpan<DreamValue> interps = state.PopCount(interpCount);
        var nextInterpIndex = 0; // If we find a prefix macro, this is what it points to
        int prevInterpIndex =
            -1; // If we find a suffix macro, this is what it points to (treating -1 as a 'null' state here)

        foreach (char c in unformattedString) {
            if (!StringFormatEncoder.Decode(c, out FormatSuffix? formatType)) {
                formattedString.Append(c);
                continue;
            }

            switch (formatType) {
                //Interp values
                case FormatSuffix.StringifyWithArticle: {
                    // TODO: use postPrefix for \th interpolation
                    formattedString.Append(interps[nextInterpIndex].Stringify());
                    prevInterpIndex = nextInterpIndex;
                    nextInterpIndex++;
                    continue;
                }
                case FormatSuffix.ReferenceOfValue: {
                    string refStr = state.Proc.RefManager.GetRefString(interps[nextInterpIndex]);
                    formattedString.Append(refStr);

                    //suffix macro marker is not updated because suffixes do not point to \ref[] interpolations
                    nextInterpIndex++;
                    continue;
                }
                case FormatSuffix.StringifyNoArticle: {
                    if (interps[nextInterpIndex].TryGetValueAsDreamObject<DreamObject>(out DreamObject? dreamObject))
                        formattedString.Append(dreamObject.GetNameUnformatted());
                    else if (interps[nextInterpIndex].TryGetValueAsString(out string? interpStr))
                        formattedString.Append(StringFormatDecoder.RemoveFormatting(interpStr));

                    // NOTE probably should put this above the TryGetAsDreamObject function and continue if formatting has occured
                    if (postPrefix != null) { // Cursed Hack
                        switch (postPrefix) {
                            case FormatSuffix.LowerRoman:
                                ToRoman(ref formattedString, interps, nextInterpIndex, false);
                                break;
                            case FormatSuffix.UpperRoman:
                                ToRoman(ref formattedString, interps, nextInterpIndex, true);
                                break;
                        }

                        postPrefix = null;
                    }

                    //Things that aren't objects or strings just print nothing in this case
                    prevInterpIndex = nextInterpIndex;
                    nextInterpIndex++;
                    continue;
                }
                case FormatSuffix.NoStringify:
                    prevInterpIndex = nextInterpIndex;
                    nextInterpIndex++;
                    break;

                //Macro values//
                //Prefix macros
                case FormatSuffix.UpperDefiniteArticle:
                case FormatSuffix.LowerDefiniteArticle: {
                    if (interps[nextInterpIndex].TryGetValueAsDreamObject<DreamObject>(out DreamObject? dreamObject))
                        if (dreamObject.TryGetVariable("name", out DreamValue objectName)) {
                            string nameStr = objectName.Stringify();
                            if (!DreamObject.StringIsProper(nameStr))
                                formattedString.Append(
                                    formatType == FormatSuffix.UpperDefiniteArticle ? "The " : "the ");

                            objectName.Dispose();
                        }

                    continue;
                }
                case FormatSuffix.UpperIndefiniteArticle:
                case FormatSuffix.LowerIndefiniteArticle: {
                    DreamValue interpValue = interps[nextInterpIndex];
                    string displayName;
                    var isPlural = false;

                    if (interpValue.TryGetValueAsDreamObject<DreamObject>(out DreamObject? dreamObject)) {
                        displayName = dreamObject.GetRawName();

                        // Aayy babe whats ya pronouns
                        if (dreamObject.TryGetVariable("gender", out DreamValue gender) &&
                            gender.TryGetValueAsString(out string? genderStr))
                            // NOTE: In Byond, this part does not work if var/gender is not a native property of this object.
                            isPlural = genderStr == "plural";

                        gender.Dispose();
                    } else if (interpValue.TryGetValueAsString(out string? interpStr)) {
                        displayName = interpStr;
                    } else {
                        break;
                    }

                    if (DreamObject.StringIsProper(displayName))
                        break; // Proper nouns don't need articles, I guess.

                    // saves some wordiness with the ternaries below
                    bool wasCapital = formatType == FormatSuffix.UpperIndefiniteArticle;

                    if (isPlural)
                        formattedString.Append(wasCapital ? "Some " : "some ");
                    else if (DreamObject.StringStartsWithVowel(displayName))
                        formattedString.Append(wasCapital ? "An " : "an ");
                    else
                        formattedString.Append(wasCapital ? "A " : "a ");

                    break;
                }
                //Suffix macros
                case FormatSuffix.UpperSubjectPronoun:
                    HandleSuffixPronoun(ref formattedString, interps, prevInterpIndex,
                        new[] {"He", "She", "They", "Tt"});
                    break;
                case FormatSuffix.LowerSubjectPronoun:
                    HandleSuffixPronoun(ref formattedString, interps, prevInterpIndex,
                        new[] {"he", "she", "they", "it"});
                    break;
                case FormatSuffix.UpperPossessiveAdjective:
                    HandleSuffixPronoun(ref formattedString, interps, prevInterpIndex,
                        new[] {"His", "Her", "Their", "Its"});
                    break;
                case FormatSuffix.LowerPossessiveAdjective:
                    HandleSuffixPronoun(ref formattedString, interps, prevInterpIndex,
                        new[] {"his", "her", "their", "its"});
                    break;
                case FormatSuffix.ObjectPronoun:
                    HandleSuffixPronoun(ref formattedString, interps, prevInterpIndex,
                        new[] {"him", "her", "them", "it"});
                    break;
                case FormatSuffix.ReflexivePronoun:
                    HandleSuffixPronoun(ref formattedString, interps, prevInterpIndex,
                        new[] {"himself", "herself", "themself", "itself"});
                    break;
                case FormatSuffix.UpperPossessivePronoun:
                    HandleSuffixPronoun(ref formattedString, interps, prevInterpIndex,
                        new[] {"His", "Hers", "Theirs", "Its"});
                    break;
                case FormatSuffix.LowerPossessivePronoun:
                    HandleSuffixPronoun(ref formattedString, interps, prevInterpIndex,
                        new[] {"his", "hers", "theirs", "its"});
                    break;
                case FormatSuffix.PluralSuffix:
                    if (interps[prevInterpIndex].TryGetValueAsFloat(out float pluralNumber) && pluralNumber.Equals(1f))
                        continue;

                    formattedString.Append("s");
                    continue;
                case FormatSuffix.OrdinalIndicator:
                    DreamValue interp = interps[prevInterpIndex];
                    if (interp.TryGetValueAsInteger(out int ordinalNumber)) {
                        // For some mystical reason byond converts \th to integers
                        // This is slightly hacky but the only reliable way I know how to replace the number
                        // Need to call stringy to make sure its the right length to cut
                        formattedString.Length -= interps[prevInterpIndex].Stringify().Length;
                        formattedString.Append(ordinalNumber);
                        switch (ordinalNumber) {
                            case 1:
                                formattedString.Append("st");
                                break;
                            case 2:
                                formattedString.Append("nd");
                                break;
                            case 3:
                                formattedString.Append("rd");
                                break;
                            default:
                                formattedString.Append("th");
                                break;
                        }
                    } else if (interp.IsNull) {
                        formattedString.Append("0th");
                    } else if (interp.TryGetValueAsString(out string? interpString)) {
                        int lastIdx = formattedString.ToString().LastIndexOf(interpString);
                        if (lastIdx != -1) { // Can this even fail?
                            formattedString.Remove(lastIdx, interpString.Length);
                            formattedString.Append("0th");
                        }
                    } else if (interp.TryGetValueAsDreamObject(out DreamObject? interpObj)) {
                        string typeStr = interpObj.ObjectDefinition.Type;
                        int lastIdx = formattedString.ToString().LastIndexOf(typeStr);
                        if (lastIdx != -1) { // Can this even fail?
                            formattedString.Remove(lastIdx, typeStr.Length);
                            formattedString.Append("0th");
                        }
                    } else {
                        // TODO: if the preceding expression value is not a float, it should be replaced with 0 (0th)
                        // we support this behavior for some non-floats but not all, so just append 0th anyways for now
                        formattedString.Append("0th");
                    }

                    continue;
                case FormatSuffix.LowerRoman:
                    postPrefix = formatType;
                    continue;
                case FormatSuffix.UpperRoman:
                    postPrefix = formatType;
                    continue;
                case FormatSuffix.Icon:
                    DreamValue iconValue = interps[nextInterpIndex];
                    if (!iconValue.TryGetValueAsDreamObject<DreamObjectAtom>(out DreamObjectAtom? atom))
                        continue;
                    if (!state.Proc.AtomManager.TryGetAppearance(atom, out ImmutableAppearance? appearance))
                        continue;

                    var entitySystemManager = IoCManager.Resolve<IEntitySystemManager>();
                    if (!entitySystemManager.TryGetEntitySystem(out ServerAppearanceSystem? appearanceSystem))
                        continue;
                    if (!appearanceSystem.AddAppearance(appearance).TryGetId(out uint? appearanceId))
                        continue;

                    // Encode the 4-byte appearance ID as characters in the string
                    var upper = (char)((ushort)(appearanceId & 0xFFFF0000) >> 16);
                    var lower = (char)(ushort)(appearanceId & 0xFFFF);
                    formattedString.Append(StringFormatting.Icon);
                    formattedString.Append(upper);
                    formattedString.Append(lower);
                    continue;
                default:
                    if (Enum.IsDefined(typeof(FormatSuffix), formatType))
                        //Likely an unimplemented text macro, ignore it
                        break;

                    throw new Exception("Invalid special character");
            }
        }

        foreach (DreamValue interp in interps)
            interp.Dispose();

        state.Push(new DreamValue(formattedString.ToString()));
        return ProcStatus.Continue;
    }

    public static ProcStatus Initial(DMProcState state) {
        using DreamValue key = state.Pop();
        using DreamValue owner = state.Pop();

        // number indices always perform a normal list access here
        if (key.TryGetValueAsInteger(out _)) {
            using DreamValue indexResult = state.GetIndex(owner, key, state);

            state.Push(indexResult);
            return ProcStatus.Continue;
        }

        if (!key.TryGetValueAsString(out string? property))
            throw new DMException("Invalid var for initial() call: " + key);

        TreeEntry treeEntry;
        if (owner.TryGetValueAsDreamObject(out DreamObject? dreamObject)) {
            switch (dreamObject) {
                // Calling initial() on a null value just returns null
                case null:
                    state.Push(DreamValue.Null);
                    return ProcStatus.Continue;
                // initial(object.vars.foo) should act like initial(object.foo)
                case DreamListVars varsList:
                    treeEntry = varsList.DreamObject.ObjectDefinition.TreeEntry;
                    break;
                default:
                    treeEntry = dreamObject.ObjectDefinition.TreeEntry;
                    break;
            }
        } else if (owner.TryGetValueAsType(out TreeEntry? ownerType)) {
            treeEntry = ownerType;
        } else {
            state.DreamManager.OptionalException<ArgumentException>(WarningCode.InitialVarOnPrimitiveException,
                "Initial() attempted to get the initial value of a variable on a primitive.");
            state.Push(DreamValue.Null);
            return ProcStatus.Continue;
        }

        if (!treeEntry.TryGetTypeVar(property, out DreamValue result)) result = DreamValue.Null;

        state.Push(result);
        return ProcStatus.Continue;
    }

    public static ProcStatus IsNull(DMProcState state) {
        using DreamValue value = state.Pop();

        state.Push(new DreamValue(value.IsNull ? 1 : 0));
        return ProcStatus.Continue;
    }

    public static ProcStatus IsInList(DMProcState state) {
        using DreamValue listValue = state.Pop();
        using DreamValue value = state.Pop();

        if (listValue.TryGetValueAsDreamObject(out DreamObject? listObject) && listObject != null) {
            IDreamList list;
            switch (listObject) {
                case DreamObjectAtom or DreamObjectWorld:
                    using (DreamValue contents = listObject.GetVariable("contents")) {
                        list = contents.MustGetValueAsDreamList();
                    }

                    break;
                case DreamObjectSavefile savefile:
                    list = new SavefileDirList(state.Proc.ObjectTree.List.ObjectDefinition, savefile);
                    break;
                case IDreamList dreamList:
                    list = dreamList;
                    break;
                default:
                    list = null;
                    break;
            }

            if (list != null)
                state.Push(new DreamValue(list.ContainsValue(value) ? 1 : 0));
            else
                // BYOND ignores all floats, strings, types, etc. here and just returns 0.
                state.Push(DreamValue.False);
        } else {
            state.Push(DreamValue.False);
        }

        return ProcStatus.Continue;
    }

    public static ProcStatus Pop(DMProcState state) {
        state.PopDrop();
        return ProcStatus.Continue;
    }

    public static ProcStatus PopReference(DMProcState state) {
        DreamReference reference = state.ReadReference();
        state.PopReference(reference);
        return ProcStatus.Continue;
    }

    public static ProcStatus PushFloat(DMProcState state) {
        float value = state.ReadFloat();

        state.Push(new DreamValue(value));
        return ProcStatus.Continue;
    }

    public static ProcStatus PushNull(DMProcState state) {
        state.Push(DreamValue.Null);
        return ProcStatus.Continue;
    }

    public static ProcStatus PushType(DMProcState state) {
        int typeId = state.ReadInt();
        TreeEntry type = state.Proc.ObjectTree.Types[typeId];

        state.Push(new DreamValue(type));
        return ProcStatus.Continue;
    }

    public static ProcStatus PushProc(DMProcState state) {
        int procId = state.ReadInt();

        state.Push(new DreamValue(state.Proc.ObjectTree.Procs[procId]));
        return ProcStatus.Continue;
    }

    public static ProcStatus PushResource(DMProcState state) {
        string resourcePath = state.ReadString();

        state.Push(new DreamValue(state.Proc.DreamResourceManager.LoadResource(resourcePath)));
        return ProcStatus.Continue;
    }

    public static ProcStatus PushString(DMProcState state) {
        state.Push(new DreamValue(state.ReadString()));
        return ProcStatus.Continue;
    }

    public static ProcStatus PushGlobalVars(DMProcState state) {
        var globalVarsList = new DreamGlobalVars(state.Proc.ObjectTree.List.ObjectDefinition);

        state.Push(new DreamValue(globalVarsList));
        globalVarsList.DecRef();
        return ProcStatus.Continue;
    }

    #endregion Values

    #region Math

    public static ProcStatus Add(DMProcState state) {
        using DreamValue second = state.Pop();
        using DreamValue first = state.Pop();

        if (first.TryGetValueAsDreamObject<DreamObject>(out DreamObject? firstDreamObject)) {
            using DreamValue result = firstDreamObject.OperatorAdd(second, state);

            state.Push(result);
            return ProcStatus.Continue;
        }

        DreamValue output = default;

        if (first.IsNull)
            output = second;
        else if (first.TryGetValueAsDreamResource(out _) || first.TryGetValueAsDreamObject<DreamObjectIcon>(out _))
            output = IconOperation(state, BlendType.Add, first, second);
        else if (first.TryGetValueAsType(out _) || first.TryGetValueAsProc(out _))
            output = default; // Always errors
        else if (second.IsNull)
            output = first;
        else
            switch (first.Type) {
                case DreamValue.DreamValueType.Float: {
                    float firstFloat = first.MustGetValueAsFloat();

                    if (second.Type == DreamValue.DreamValueType.Float)
                        output = new DreamValue(firstFloat + second.MustGetValueAsFloat());

                    break;
                }
                case DreamValue.DreamValueType.String when second.Type == DreamValue.DreamValueType.String:
                    output = new DreamValue(first.MustGetValueAsString() + second.MustGetValueAsString());
                    break;
            }

        if (output.Type != default)
            state.Push(output);
        else
            ThrowInvalidAddOperation(first, second);

        return ProcStatus.Continue;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowInvalidAddOperation(DreamValue first, DreamValue second) {
        throw new DMException("Invalid add operation on " + first + " and " + second);
    }

    public static ProcStatus Append(DMProcState state) {
        using DreamValue result = AppendHelper(state);

        state.Push(result);
        return ProcStatus.Continue;
    }

    /// <summary>
    ///     Identical to <see cref="Append" /> except it never pushes the result to the stack
    /// </summary>
    public static ProcStatus AppendNoPush(DMProcState state) {
        AppendHelper(state).Dispose();
        return ProcStatus.Continue;
    }

    [MustDisposeResource]
    private static DreamValue AppendHelper(DMProcState state) {
        DreamReference reference = state.ReadReference();
        using DreamValue second = state.Pop();
        using DreamValue first = state.GetReferenceValue(reference, true);

        DreamValue result;
        if (first.TryGetValueAsDreamResource(out _) || first.TryGetValueAsDreamObject<DreamObjectIcon>(out _)) {
            result = IconOperation(state, BlendType.Add, first, second);
        } else if (first.TryGetValueAsDreamObject(out DreamObject? firstObj)) {
            if (firstObj != null) {
                state.PopReference(reference);
                return firstObj.OperatorAppend(second);
            }

            result = second;
            result.IncRef();
        } else if (!second.IsNull) {
            switch (first.Type) {
                case DreamValue.DreamValueType.Float when second.Type == DreamValue.DreamValueType.Float:
                    result = new DreamValue(first.MustGetValueAsFloat() + second.MustGetValueAsFloat());
                    break;
                case DreamValue.DreamValueType.String when second.Type == DreamValue.DreamValueType.String:
                    result = new DreamValue(first.MustGetValueAsString() + second.MustGetValueAsString());
                    break;
                default:
                    throw new DMException("Invalid append operation on " + first + " and " + second);
            }
        } else {
            result = first;
            result.IncRef();
        }

        state.AssignReference(reference, result);
        return result;
    }

    public static ProcStatus Increment(DMProcState state) {
        return IncrementDecrement(state, 1, true);
    }

    public static ProcStatus PreIncrement(DMProcState state) {
        return IncrementDecrement(state, 1, false);
    }

    public static ProcStatus Decrement(DMProcState state) {
        return IncrementDecrement(state, -1, true);
    }

    public static ProcStatus PreDecrement(DMProcState state) {
        return IncrementDecrement(state, -1, false);
    }

    private static ProcStatus IncrementDecrement(DMProcState state, float adjustment, bool returnPrevious) {
        DreamReference reference = state.ReadReference();
        using DreamValue value = state.GetReferenceValue(reference, true);
        DreamValue result = new(value.UnsafeGetValueAsFloat() + adjustment);

        // BYOND coerces every non-number including null to 0 for ++ and --
        state.AssignReference(reference, result);

        state.Push(returnPrevious ? value : result);
        return ProcStatus.Continue;
    }

    public static ProcStatus BitAnd(DMProcState state) {
        using DreamValue second = state.Pop();
        using DreamValue first = state.Pop();

        if (!first.IsDreamObject<DreamList>() && !first.IsNull && !second.IsNull) {
            state.Push(new DreamValue(first.MustGetValueAsInteger() & second.MustGetValueAsInteger()));
        } else if (first.TryGetValueAsDreamList(out DreamList? list)) {
            DreamList newList = state.Proc.ObjectTree.CreateList();
            Dictionary<DreamValue, DreamValue> associativeValues = list.GetAssociativeValues();
            second.TryGetValueAsDreamList(out DreamList? secondList);
            Dictionary<DreamValue, int>? remainingValues = null;
            var scalarAvailable = true;

            if (secondList != null) {
                remainingValues = new Dictionary<DreamValue, int>(secondList.GetLength());
                foreach (DreamValue value in secondList.EnumerateValues()) {
                    remainingValues.TryGetValue(value, out int count);
                    remainingValues[value] = count + 1;
                }
            }

            // BYOND intersection retains min(left count, right count) occurrences in left-list order
            // associative values come from the retained left pair

            foreach (DreamValue value in list.EnumerateValues()) {
                bool retained;
                if (remainingValues != null) {
                    retained = remainingValues.TryGetValue(value, out int count) && count > 0;
                    if (retained)
                        remainingValues[value] = count - 1;
                } else {
                    retained = scalarAvailable && value == second;
                    if (retained)
                        scalarAvailable = false;
                }

                if (!retained)
                    continue;

                newList.AddValue(value);
                if (associativeValues.TryGetValue(value, out DreamValue associatedValue))
                    newList.SetValue(value, associatedValue);
            }

            state.Push(new DreamValue(newList));
            newList.DecRef();
        } else {
            state.Push(new DreamValue(0));
        }

        return ProcStatus.Continue;
    }

    public static ProcStatus BitNot(DMProcState state) {
        using DreamValue input = state.Pop();

        if (input.TryGetValueAsInteger(out int value)) {
            state.Push(new DreamValue(~value & 0xFFFFFF));
        } else {
            if (input.TryGetValueAsDreamObject<DreamObjectMatrix>(out _)) // TODO ~ on /matrix
                throw new NotImplementedException("/matrix does not support the '~' operator yet");

            state.Push(new DreamValue(16777215)); // 2^24 - 1
        }

        return ProcStatus.Continue;
    }

    public static ProcStatus BitOr(DMProcState state) { // x | y
        using DreamValue second = state.Pop();
        using DreamValue first = state.Pop();

        if (first.IsNull) {
            state.Push(second);
        } else if (first.TryGetValueAsDreamObject<DreamObject>(out DreamObject? firstDreamObject)) { // Object | y
            if (!first.IsNull) {
                using DreamValue result = firstDreamObject.OperatorOr(second, state);

                state.Push(result);
            } else {
                state.Push(DreamValue.Null);
            }
        } else if (!second.IsNull) { // Non-Object | y
            switch (first.Type) {
                case DreamValue.DreamValueType.Float when second.Type == DreamValue.DreamValueType.Float:
                    state.Push(new DreamValue(first.MustGetValueAsInteger() | second.MustGetValueAsInteger()));
                    break;
                default:
                    throw new DMException("Invalid or operation on " + first + " and " + second);
            }
        } else if (first.TryGetValueAsInteger(out int firstInt)) {
            state.Push(new DreamValue(firstInt));
        } else {
            throw new DMException("Invalid or operation on " + first + " and " + second);
        }

        return ProcStatus.Continue;
    }

    public static ProcStatus BitShiftLeft(DMProcState state) {
        using DreamValue second = state.Pop();
        using DreamValue first = state.Pop();

        switch (first.Type) {
            case DreamValue.DreamValueType.DreamObject when first.IsNull:
                state.Push(new DreamValue(0));
                break;
            case DreamValue.DreamValueType.Float when second.Type == DreamValue.DreamValueType.Float:
                state.Push(new DreamValue(SharedOperations.BitShiftLeft(first.MustGetValueAsInteger(),
                    second.MustGetValueAsInteger())));
                break;
            case DreamValue.DreamValueType.Float when second.IsNull:
                state.Push(new DreamValue(first.MustGetValueAsInteger()));
                break;
            default:
                throw new DMException($"Invalid bit shift left operation on {first} and {second}");
        }

        return ProcStatus.Continue;
    }

    public static ProcStatus BitShiftLeftReference(DMProcState state) {
        DreamReference reference = state.ReadReference();
        using DreamValue second = state.Pop();
        using DreamValue first = state.GetReferenceValue(reference, true);

        DreamValue result;
        switch (first.Type) {
            case DreamValue.DreamValueType.DreamObject when first.IsNull:
                result = new DreamValue(0);
                break;
            case DreamValue.DreamValueType.Float when second.Type == DreamValue.DreamValueType.Float:
                result = new DreamValue(SharedOperations.BitShiftLeft(first.MustGetValueAsInteger(),
                    second.MustGetValueAsInteger()));
                break;
            case DreamValue.DreamValueType.Float when second.IsNull:
                result = new DreamValue(first.MustGetValueAsInteger());
                break;
            default:
                throw new DMException($"Invalid bit shift left operation on {first} and {second}");
        }

        state.AssignReference(reference, result);
        state.Push(result);
        return ProcStatus.Continue;
    }

    public static ProcStatus BitShiftRight(DMProcState state) {
        using DreamValue second = state.Pop();
        using DreamValue first = state.Pop();

        switch (first.Type) {
            case DreamValue.DreamValueType.DreamObject when first.IsNull:
                state.Push(new DreamValue(0));
                break;
            case DreamValue.DreamValueType.Float when second.Type == DreamValue.DreamValueType.Float:
                state.Push(new DreamValue(SharedOperations.BitShiftRight(first.MustGetValueAsInteger(),
                    second.MustGetValueAsInteger())));
                break;
            case DreamValue.DreamValueType.Float when second.IsNull:
                state.Push(new DreamValue(first.MustGetValueAsInteger()));
                break;
            default:
                throw new DMException($"Invalid bit shift right operation on {first} and {second}");
        }

        return ProcStatus.Continue;
    }

    public static ProcStatus BitShiftRightReference(DMProcState state) {
        DreamReference reference = state.ReadReference();
        using DreamValue second = state.Pop();
        using DreamValue first = state.GetReferenceValue(reference, true);

        DreamValue result;
        switch (first.Type) {
            case DreamValue.DreamValueType.DreamObject when first.IsNull:
                result = new DreamValue(0);
                break;
            case DreamValue.DreamValueType.Float when second.Type == DreamValue.DreamValueType.Float:
                result = new DreamValue(SharedOperations.BitShiftRight(first.MustGetValueAsInteger(),
                    second.MustGetValueAsInteger()));
                break;
            case DreamValue.DreamValueType.Float when second.IsNull:
                result = new DreamValue(first.MustGetValueAsInteger());
                break;
            default:
                throw new DMException($"Invalid bit shift right operation on {first} and {second}");
        }

        state.AssignReference(reference, result);
        state.Push(result);
        return ProcStatus.Continue;
    }

    public static ProcStatus BitXor(DMProcState state) {
        using DreamValue second = state.Pop();
        using DreamValue first = state.Pop();
        using DreamValue result = BitXorValues(state.Proc.ObjectTree, first, second);

        state.Push(result);
        return ProcStatus.Continue;
    }

    public static ProcStatus BitXorReference(DMProcState state) {
        using DreamValue second = state.Pop();
        DreamReference reference = state.ReadReference();
        using DreamValue first = state.GetReferenceValue(reference, true);

        if (first.TryGetValueAsDreamList(out DreamList? firstList)) {
            using DreamValue replacement = BitXorAssignmentValues(state.Proc.ObjectTree, firstList, second);
            DreamList replacementList = replacement.MustGetValueAsDreamList();
            ReplaceListContents(firstList, replacementList);

            // BYOND mutates and returns the existing list object
            firstList.IncRef();
            using var inPlaceResult = new DreamValue(firstList);
            state.PopReference(reference);
            state.Push(inPlaceResult);
            return ProcStatus.Continue;
        }

        using DreamValue result = BitXorValues(state.Proc.ObjectTree, first, second);

        state.AssignReference(reference, result);
        state.Push(result);
        return ProcStatus.Continue;
    }

    public static ProcStatus BooleanAnd(DMProcState state) {
        using DreamValue a = state.Pop();
        int jumpPosition = state.ReadInt();

        if (!a.IsTruthy()) {
            state.Push(a);
            state.Jump(jumpPosition);
        }

        return ProcStatus.Continue;
    }

    public static ProcStatus BooleanNot(DMProcState state) {
        using DreamValue value = state.Pop();

        state.Push(new DreamValue(value.IsTruthy() ? 0 : 1));
        return ProcStatus.Continue;
    }

    public static ProcStatus BooleanOr(DMProcState state) {
        using DreamValue a = state.Pop();
        int jumpPosition = state.ReadInt();

        if (a.IsTruthy()) {
            state.Push(a);
            state.Jump(jumpPosition);
        }

        return ProcStatus.Continue;
    }

    public static ProcStatus Combine(DMProcState state) {
        DreamReference reference = state.ReadReference();
        using DreamValue second = state.Pop();
        using DreamValue first = state.GetReferenceValue(reference, true);

        DreamValue result;
        if (first.TryGetValueAsDreamObject(out DreamObject? firstObj)) {
            if (firstObj != null) {
                using DreamValue opResult = firstObj.OperatorCombine(second);

                state.PopReference(reference);
                state.Push(opResult);
                return ProcStatus.Continue;
            }

            result = second;
        } else if (!second.IsNull) {
            if (first.TryGetValueAsInteger(out int firstInt) && second.TryGetValueAsInteger(out int secondInt))
                result = new DreamValue(firstInt | secondInt);
            else if (first.IsNull)
                result = second;
            else
                throw new DMException("Invalid combine operation on " + first + " and " + second);
        } else if (first.Type == DreamValue.DreamValueType.Float) {
            result = first;
        } else {
            throw new DMException("Invalid combine operation on " + first + " and " + second);
        }

        state.AssignReference(reference, result);
        state.Push(result);
        return ProcStatus.Continue;
    }

    public static ProcStatus Divide(DMProcState state) {
        using DreamValue second = state.Pop();
        using DreamValue first = state.Pop();

        switch (first.Type) {
            case DreamValue.DreamValueType.DreamObject when first.IsNull:
                state.Push(new DreamValue(0));
                break;
            case DreamValue.DreamValueType.Float when second.IsNull:
                throw new DMException($"Attempted to divide {first} by null");
            case DreamValue.DreamValueType.Float when second.Type == DreamValue.DreamValueType.Float:
                float secondFloat = second.MustGetValueAsFloat();
                if (secondFloat == 0) throw new DMException("Division by zero");

                state.Push(new DreamValue(first.MustGetValueAsFloat() / secondFloat));
                break;
            case DreamValue.DreamValueType.DreamObject:
                DreamValue result = first.MustGetValueAsDreamObject()!.OperatorDivide(second, state);

                state.Push(result);
                result.Dispose();
                break;
            default:
                throw new DMException($"Invalid divide operation on {first} and {second}");
        }

        return ProcStatus.Continue;
    }

    public static ProcStatus DivideReference(DMProcState state) {
        DreamReference reference = state.ReadReference();
        using DreamValue second = state.Pop();
        using DreamValue first = state.GetReferenceValue(reference, true);

        if (first.TryGetValueAsFloat(out float firstFloat) || first.IsNull) {
            float secondFloat = second.UnsafeGetValueAsFloat(); // Non-numbers are always treated as 0 here
            var result = new DreamValue(firstFloat / secondFloat);
            state.AssignReference(reference, result);
            state.Push(result);
        } else if (first.TryGetValueAsDreamObject<DreamObject>(out DreamObject? firstDreamObject)) {
            using DreamValue result = firstDreamObject.OperatorDivideRef(second, state);

            state.AssignReference(reference, result);
            state.Push(result);
        } else {
            throw new DMException($"Invalid divide operation on {first} and {second}");
        }

        return ProcStatus.Continue;
    }

    public static ProcStatus Mask(DMProcState state) {
        DreamReference reference = state.ReadReference();
        using DreamValue second = state.Pop();
        using DreamValue first = state.GetReferenceValue(reference, true);

        DreamValue result;
        switch (first.Type) {
            case DreamValue.DreamValueType.DreamObject when !first.IsNull: {
                using DreamValue opResult = first.MustGetValueAsDreamObject()!.OperatorMask(second);

                state.PopReference(reference);
                state.Push(opResult);
                return ProcStatus.Continue;
            }
            case DreamValue.DreamValueType.DreamObject: // null
                result = new DreamValue(0);
                break;
            case DreamValue.DreamValueType.Float when second.Type == DreamValue.DreamValueType.Float:
                result = new DreamValue(first.MustGetValueAsInteger() & second.MustGetValueAsInteger());
                break;
            case DreamValue.DreamValueType.Float when second.IsNull:
                result = new DreamValue(0);
                break;
            default:
                throw new DMException("Invalid mask operation on " + first + " and " + second);
        }

        state.AssignReference(reference, result);
        state.Push(result);
        return ProcStatus.Continue;
    }

    public static ProcStatus Modulus(DMProcState state) {
        using DreamValue second = state.Pop();
        using DreamValue first = state.Pop();

        state.Push(ModulusValues(first, second));
        return ProcStatus.Continue;
    }

    public static ProcStatus ModulusModulus(DMProcState state) {
        using DreamValue second = state.Pop();
        using DreamValue first = state.Pop();

        state.Push(ModulusModulusValues(first, second));
        return ProcStatus.Continue;
    }

    public static ProcStatus ModulusReference(DMProcState state) {
        using DreamValue second = state.Pop();
        DreamReference reference = state.ReadReference();
        using DreamValue first = state.GetReferenceValue(reference, true);
        DreamValue result = ModulusValues(first, second);

        state.AssignReference(reference, result);
        state.Push(result);
        return ProcStatus.Continue;
    }

    public static ProcStatus ModulusModulusReference(DMProcState state) {
        using DreamValue second = state.Pop();
        DreamReference reference = state.ReadReference();
        using DreamValue first = state.GetReferenceValue(reference, true);
        DreamValue result = ModulusModulusValues(first, second);

        state.AssignReference(reference, result);
        state.Push(result);
        return ProcStatus.Continue;
    }

    public static ProcStatus Multiply(DMProcState state) {
        using DreamValue second = state.Pop();
        using DreamValue first = state.Pop();

        if (first.TryGetValueAsFloat(out float firstFloat) || first.IsNull) {
            float secondFloat = second.UnsafeGetValueAsFloat(); // Non-numbers are always treated as 0 here
            state.Push(new DreamValue(firstFloat * secondFloat));
        } else if (first.TryGetValueAsDreamObject<DreamObject>(out DreamObject? firstDreamObject)) {
            using DreamValue result = firstDreamObject.OperatorMultiply(second, state);

            state.Push(result);
        } else {
            throw new DMException($"Invalid multiply operation on {first} and {second}");
        }

        return ProcStatus.Continue;
    }

    public static ProcStatus MultiplyReference(DMProcState state) {
        DreamReference reference = state.ReadReference();
        using DreamValue second = state.Pop();
        using DreamValue first = state.GetReferenceValue(reference, true);
        DreamValue result;

        if (first.TryGetValueAsFloat(out float firstFloat) || first.IsNull) {
            float secondFloat = second.UnsafeGetValueAsFloat(); // Non-numbers are always treated as 0 here

            result = new DreamValue(firstFloat * secondFloat);
        } else if (first.TryGetValueAsDreamResource(out _) || first.TryGetValueAsDreamObject<DreamObjectIcon>(out _)) {
            result = IconOperation(state, BlendType.Multiply, first, second);
        } else if (first.TryGetValueAsDreamObject<DreamObject>(out DreamObject? firstDreamObject)) {
            result = firstDreamObject.OperatorMultiplyRef(second, state);
        } else {
            throw new DMException($"Invalid multiply operation on {first} and {second}");
        }

        state.AssignReference(reference, result);
        state.Push(result);
        result.Dispose();
        return ProcStatus.Continue;
    }

    public static ProcStatus Negate(DMProcState state) {
        using DreamValue first = state.Pop();
        float value = first.UnsafeGetValueAsFloat();

        state.Push(new DreamValue(-value));
        return ProcStatus.Continue;
    }

    public static ProcStatus Power(DMProcState state) {
        using DreamValue second = state.Pop();
        using DreamValue first = state.Pop();

        if (!first.TryGetValueAsFloat(out float floatFirst) && !first.IsNull)
            throw new DMException($"Invalid power operation on {first} and {second}");

        float floatSecond = second.UnsafeGetValueAsFloat(); // Non-numbers treated as 0 here

        state.Push(new DreamValue(MathF.Pow(floatFirst, floatSecond)));
        return ProcStatus.Continue;
    }

    public static ProcStatus Remove(DMProcState state) {
        DreamReference reference = state.ReadReference();
        using DreamValue second = state.Pop();
        using DreamValue first = state.GetReferenceValue(reference, true);

        DreamValue result;
        switch (first.Type) {
            case DreamValue.DreamValueType.DreamObject when !first.IsNull: {
                using DreamValue opResult = first.MustGetValueAsDreamObject()!.OperatorRemove(second);

                state.PopReference(reference);
                state.Push(opResult);
                return ProcStatus.Continue;
            }
            case DreamValue.DreamValueType.DreamObject when first.IsNull: // null is treated as 0
            case DreamValue.DreamValueType.Float:
                if (second.Type != DreamValue.DreamValueType.Float && !second.IsNull)
                    goto default;

                // UnsafeGetValueAsFloat() so that null is treated as 0.
                result = new DreamValue(first.UnsafeGetValueAsFloat() - second.UnsafeGetValueAsFloat());
                break;
            default:
                throw new DMException($"Invalid remove operation on {first} and {second}");
        }

        state.AssignReference(reference, result);
        state.Push(result);
        return ProcStatus.Continue;
    }

    public static ProcStatus Subtract(DMProcState state) {
        using DreamValue second = state.Pop();
        using DreamValue first = state.Pop();
        DreamValue output = default;

        if (first.TryGetValueAsFloat(out float firstFloat) || first.IsNull) {
            if (second.TryGetValueAsFloat(out float secondFloat) || second.IsNull)
                output = new DreamValue(firstFloat - secondFloat);
        } else if (first.TryGetValueAsDreamObject<DreamObject>(out DreamObject? firstObject)) {
            output = firstObject.OperatorSubtract(second, state);
        }

        if (output.Type != default) {
            state.Push(output);
            output.Dispose();
        } else {
            throw new DMException($"Invalid subtract operation on {first} and {second}");
        }

        return ProcStatus.Continue;
    }

    #endregion Math

    #region Comparisons

    public static ProcStatus CompareEquals(DMProcState state) {
        using DreamValue second = state.Pop();
        using DreamValue first = state.Pop();

        state.Push(new DreamValue(IsEqual(first, second) ? 1 : 0));
        return ProcStatus.Continue;
    }

    public static ProcStatus CompareEquivalent(DMProcState state) {
        using DreamValue second = state.Pop();
        using DreamValue first = state.Pop();

        state.Push(new DreamValue(IsEquivalent(first, second) ? 1 : 0));
        return ProcStatus.Continue;
    }

    public static ProcStatus CompareGreaterThan(DMProcState state) {
        using DreamValue second = state.Pop();
        using DreamValue first = state.Pop();

        if (TryGetReferenceComparisonResult(first, second, out DreamValue referenceResult)) {
            state.Push(referenceResult);
            return ProcStatus.Continue;
        }

        state.Push(new DreamValue(IsGreaterThan(first, second) ? 1 : 0));
        return ProcStatus.Continue;
    }

    public static ProcStatus CompareGreaterThanOrEqual(DMProcState state) {
        using DreamValue second = state.Pop();
        using DreamValue first = state.Pop();
        DreamValue result;

        if (TryGetReferenceComparisonResult(first, second, out DreamValue referenceResult))
            result = referenceResult;
        else if (first.TryGetValueAsFloat(out float lhs) && lhs == 0.0 && second.IsNull) result = new DreamValue(1);
        else switch (first.IsNull)
        {
            case true when second.TryGetValueAsFloat(out float rhs) && rhs == 0.0:
            case true when second.TryGetValueAsString(out string? s) && s == "":
                result = new DreamValue(1);
                break;
            default:
                result = new DreamValue(IsEqual(first, second) || IsGreaterThan(first, second) ? 1 : 0);
                break;
        }

        state.Push(result);
        return ProcStatus.Continue;
    }

    public static ProcStatus CompareLessThan(DMProcState state) {
        using DreamValue second = state.Pop();
        using DreamValue first = state.Pop();

        if (TryGetReferenceComparisonResult(first, second, out DreamValue referenceResult)) {
            state.Push(referenceResult);
            return ProcStatus.Continue;
        }

        state.Push(new DreamValue(IsLessThan(first, second) ? 1 : 0));
        return ProcStatus.Continue;
    }

    public static ProcStatus CompareLessThanOrEqual(DMProcState state) {
        using DreamValue second = state.Pop();
        using DreamValue first = state.Pop();
        DreamValue result;

        if (TryGetReferenceComparisonResult(first, second, out DreamValue referenceResult))
            result = referenceResult;
        else if (first.TryGetValueAsFloat(out float lhs) && lhs == 0.0 && second.IsNull) result = new DreamValue(1);
        else switch (first.IsNull)
        {
            case true when second.TryGetValueAsFloat(out float rhs) && rhs == 0.0:
            case true when second.TryGetValueAsString(out string? s) && s == "":
                result = new DreamValue(1);
                break;
            default:
                result = new DreamValue(IsEqual(first, second) || IsLessThan(first, second) ? 1 : 0);
                break;
        }

        state.Push(result);
        return ProcStatus.Continue;
    }

    public static ProcStatus CompareNotEquals(DMProcState state) {
        using DreamValue second = state.Pop();
        using DreamValue first = state.Pop();

        state.Push(new DreamValue(IsEqual(first, second) ? 0 : 1));
        return ProcStatus.Continue;
    }

    public static ProcStatus CompareNotEquivalent(DMProcState state) {
        using DreamValue second = state.Pop();
        using DreamValue first = state.Pop();

        state.Push(new DreamValue(IsEquivalent(first, second) ? 0 : 1));
        return ProcStatus.Continue;
    }

    public static ProcStatus IsInRange(DMProcState state) {
        DreamValue end = state.Pop();
        DreamValue start = state.Pop();
        DreamValue var = state.Pop();

        if (var.Type != DreamValue.DreamValueType.Float) var = new DreamValue(0f);
        if (start.Type != DreamValue.DreamValueType.Float) start = new DreamValue(0f);
        if (end.Type != DreamValue.DreamValueType.Float) end = new DreamValue(0f);

        bool inRange = (IsEqual(start, var) || IsLessThan(start, var)) && (IsEqual(var, end) || IsLessThan(var, end));
        state.Push(new DreamValue(inRange ? 1 : 0));
        end.Dispose();
        start.Dispose();
        var.Dispose();
        return ProcStatus.Continue;
    }

    public static ProcStatus AsType(DMProcState state) {
        using DreamValue typeValue = state.Pop();
        using DreamValue value = state.Pop();

        state.Push(TypecheckHelper(typeValue, value, true));

        return ProcStatus.Continue;
    }

    public static ProcStatus IsType(DMProcState state) {
        using DreamValue typeValue = state.Pop();
        using DreamValue value = state.Pop();

        state.Push(TypecheckHelper(typeValue, value, false));

        return ProcStatus.Continue;
    }

    private static DreamValue TypecheckHelper(DreamValue typeValue, DreamValue value, bool doCast) {
        // astype() returns null, istype() returns false
        DreamValue nullOrFalse = doCast ? DreamValue.Null : DreamValue.False;
        TreeEntry? type;

        if (typeValue.TryGetValueAsDreamObject(out DreamObject? typeObject)) {
            if (typeObject == null) return nullOrFalse;

            type = typeObject.ObjectDefinition.TreeEntry;
        } else if (typeValue.TryGetValueAsAppearance(out _)) {
            // /image matches an appearance
            if (value.TryGetValueAsDreamObject<DreamObjectImage>(out DreamObjectImage? imageObject))
                return doCast ? new DreamValue(imageObject) : DreamValue.True;

            return nullOrFalse;
        } else if (!typeValue.TryGetValueAsType(out type)) {
            return nullOrFalse;
        }

        if (value.TryGetValueAsDreamObject(out DreamObject? dreamObject) && dreamObject != null)
            if (dreamObject.IsSubtypeOf(type))
                return doCast ? new DreamValue(dreamObject) : DreamValue.True;

        return nullOrFalse;
    }

    #endregion Comparisons

    #region Flow

    public static ProcStatus Call(DMProcState state) {
        DreamReference procRef = state.ReadReference();
        DMProcState.DMStackArgumentInfo argumentInfo = state.ReadProcArguments();

        DreamObject instance;
        DreamProc proc;
        switch (procRef.Type) {
            case DMReference.Type.Self: {
                instance = state.Instance;
                proc = state.Proc;
                break;
            }
            case DMReference.Type.SuperProc: {
                instance = state.Instance;
                proc = state.Proc.SuperProc;

                if (proc == null) {
                    //Attempting to call a super proc where there is none will just return null
                    state.Push(DreamValue.Null);
                    return ProcStatus.Continue;
                }

                break;
            }
            case DMReference.Type.GlobalProc: {
                instance = null;
                proc = state.Proc.ObjectTree.Procs[procRef.Value];

                break;
            }
            case DMReference.Type.SrcProc: {
                instance = state.Instance;
                if (!instance.TryGetProc(state.ResolveString(procRef.Value), out proc))
                    throw new DMException(
                        $"Type {instance.ObjectDefinition.Type} has no proc called \"{state.ResolveString(procRef.Value)}\"");

                break;
            }
            default: throw new Exception($"Invalid proc reference type {procRef.Type}");
        }

        DreamProcArguments arguments = state.PopProcArguments(proc, argumentInfo);

        return state.Call(proc, instance, arguments);
    }

    public static ProcStatus CallStatement(DMProcState state) {
        DMProcState.DMStackArgumentInfo argumentsInfo = state.ReadProcArguments();
        using DreamValue source = state.Pop();

        switch (source.Type) {
            case DreamValue.DreamValueType.DreamObject: {
                DreamObject? dreamObject = source.MustGetValueAsDreamObject();
                using DreamValue procId = state.Pop();
                DreamProc? proc = null;

                switch (procId.Type) {
                    case DreamValue.DreamValueType.String:
                        proc = dreamObject?.GetProc(procId.MustGetValueAsString());
                        break;
                    case DreamValue.DreamValueType.DreamProc: {
                        proc = procId.MustGetValueAsProc();
                        break;
                    }
                }

                if (proc != null) {
                    DreamProcArguments arguments = state.PopProcArguments(proc, argumentsInfo);

                    return state.Call(proc, dreamObject, arguments);
                }

                throw new DMException($"Invalid proc ({procId} on {dreamObject})");
            }
            case DreamValue.DreamValueType.DreamProc: {
                DreamProc proc = source.MustGetValueAsProc();

                DreamProcArguments arguments = state.PopProcArguments(proc, argumentsInfo);

                return state.Call(proc, state.Instance, arguments);
            }
            case DreamValue.DreamValueType.String:
                // DLL Invoke
                return CallExt(state, source, argumentsInfo);

            default:
                throw new DMException($"Call statement has an invalid source ({source})");
        }
    }

    public static ProcStatus Error(DMProcState state) {
        throw new Exception("Reached an error opcode");
    }

    public static ProcStatus Invalid(DMProcState state) {
        throw new Exception("Reached an invalid opcode!");
    }

    public static ProcStatus Jump(DMProcState state) {
        int position = state.ReadInt();

        state.Jump(position);
        return ProcStatus.Continue;
    }

    public static ProcStatus JumpIfFalse(DMProcState state) {
        int position = state.ReadInt();
        using DreamValue value = state.Pop();

        if (!value.IsTruthy()) state.Jump(position);

        return ProcStatus.Continue;
    }

    public static ProcStatus JumpIfNull(DMProcState state) {
        int position = state.ReadInt();

        if (state.Peek().IsNull) {
            state.PopDrop();
            state.Jump(position);
        }

        return ProcStatus.Continue;
    }

    public static ProcStatus JumpIfNullNoPop(DMProcState state) {
        int position = state.ReadInt();

        if (state.Peek().IsNull) state.Jump(position);

        return ProcStatus.Continue;
    }

    public static ProcStatus JumpIfTrueReference(DMProcState state) {
        DreamReference reference = state.ReadReference();
        int position = state.ReadInt();

        using DreamValue value = state.GetReferenceValue(reference, true);

        if (value.IsTruthy()) {
            state.PopReference(reference);
            state.Push(value);
            state.Jump(position);
        }

        return ProcStatus.Continue;
    }

    public static ProcStatus JumpIfFalseReference(DMProcState state) {
        DreamReference reference = state.ReadReference();
        int position = state.ReadInt();

        using DreamValue value = state.GetReferenceValue(reference, true);

        if (!value.IsTruthy()) {
            state.PopReference(reference);
            state.Push(value);
            state.Jump(position);
        }

        return ProcStatus.Continue;
    }

    public static ProcStatus DereferenceField(DMProcState state) {
        string name = state.ReadString();
        using DreamValue owner = state.Pop();
        using DreamValue value = state.DereferenceField(owner, name);

        state.Push(value);
        return ProcStatus.Continue;
    }

    public static ProcStatus Return(DMProcState state) {
        using DreamValue returnValue = state.Pop();

        state.SetReturn(returnValue);
        return ProcStatus.Returned;
    }

    public static ProcStatus Throw(DMProcState state) {
        using DreamValue value = state.Pop();

        throw new DMThrowException(value);
    }

    public static ProcStatus Try(DMProcState state) {
        int catchPosition = state.ReadInt();
        DreamReference exceptionVarRef = state.ReadReference();
        if (exceptionVarRef.Type != DMReference.Type.Local)
            throw new Exception(
                $"The reference to place a caught exception into must be a local. {exceptionVarRef} is not valid.");

        state.StartTryBlock(catchPosition, exceptionVarRef.Value);
        return ProcStatus.Continue;
    }

    public static ProcStatus TryNoValue(DMProcState state) {
        state.StartTryBlock(state.ReadInt());
        return ProcStatus.Continue;
    }

    public static ProcStatus EndTry(DMProcState state) {
        state.EndTryBlock();
        return ProcStatus.Continue;
    }

    public static ProcStatus Sin(DMProcState state) {
        float x = state.UnsafePopAsFloat();
        float result = SharedOperations.Sin(x);

        state.Push(new DreamValue(result));
        return ProcStatus.Continue;
    }

    public static ProcStatus Cos(DMProcState state) {
        float x = state.UnsafePopAsFloat();
        float result = SharedOperations.Cos(x);

        state.Push(new DreamValue(result));
        return ProcStatus.Continue;
    }

    public static ProcStatus Tan(DMProcState state) {
        float x = state.UnsafePopAsFloat();
        float result = SharedOperations.Tan(x);

        state.Push(new DreamValue(result));
        return ProcStatus.Continue;
    }

    public static ProcStatus ArcSin(DMProcState state) {
        float x = state.UnsafePopAsFloat();
        float result = SharedOperations.ArcSin(x);

        state.Push(new DreamValue(result));
        return ProcStatus.Continue;
    }

    public static ProcStatus ArcCos(DMProcState state) {
        float x = state.UnsafePopAsFloat();
        float result = SharedOperations.ArcCos(x);

        state.Push(new DreamValue(result));
        return ProcStatus.Continue;
    }

    public static ProcStatus ArcTan(DMProcState state) {
        float a = state.UnsafePopAsFloat();
        float result = SharedOperations.ArcTan(a);

        state.Push(new DreamValue(result));
        return ProcStatus.Continue;
    }

    public static ProcStatus ArcTan2(DMProcState state) {
        float y = state.UnsafePopAsFloat();
        float x = state.UnsafePopAsFloat();
        float result = SharedOperations.ArcTan(x, y);

        state.Push(new DreamValue(result));
        return ProcStatus.Continue;
    }

    public static ProcStatus Sqrt(DMProcState state) {
        float a = state.UnsafePopAsFloat();
        float result = SharedOperations.Sqrt(a);

        state.Push(new DreamValue(result));
        return ProcStatus.Continue;
    }

    public static ProcStatus Log(DMProcState state) {
        float baseValue = state.UnsafePopAsFloat();
        float value = state.UnsafePopAsFloat();

        if (value <= 0 || float.IsNaN(value) || baseValue <= 0 || baseValue.Equals(1f) || float.IsNaN(baseValue))
            throw new Exception($"log({baseValue},{value}) is not computable");

        float result = SharedOperations.Log(value, baseValue);

        state.Push(new DreamValue(result));
        return ProcStatus.Continue;
    }

    public static ProcStatus LogE(DMProcState state) {
        float y = state.UnsafePopAsFloat();
        float result = SharedOperations.Log(y);

        if (y <= 0 || float.IsNaN(y))
            throw new Exception($"log({y}) is not computable");

        state.Push(new DreamValue(result));
        return ProcStatus.Continue;
    }

    public static ProcStatus Abs(DMProcState state) {
        float a = state.UnsafePopAsFloat();
        float result = SharedOperations.Abs(a);

        state.Push(new DreamValue(result));
        return ProcStatus.Continue;
    }

    public static ProcStatus SwitchCase(DMProcState state) {
        int casePosition = state.ReadInt();
        using DreamValue testValue = state.Pop();
        using DreamValue value = state.Pop();

        if (IsEqual(value, testValue))
            state.Jump(casePosition);
        else
            state.Push(value);

        return ProcStatus.Continue;
    }

    public static ProcStatus SwitchCaseRange(DMProcState state) {
        int casePosition = state.ReadInt();
        using DreamValue rangeUpper = state.Pop();
        using DreamValue rangeLower = state.Pop();
        using DreamValue value = state.Pop();

        bool matchesLower = IsGreaterThan(value, rangeLower) || IsEqual(value, rangeLower);
        bool matchesUpper = IsLessThan(value, rangeUpper) || IsEqual(value, rangeUpper);
        if (matchesLower && matchesUpper)
            state.Jump(casePosition);
        else
            state.Push(value);

        return ProcStatus.Continue;
    }

    //Copy & run the interpreter in a new thread
    //Jump the current thread to after the spawn's code
    public static ProcStatus Spawn(DMProcState state) {
        int jumpTo = state.ReadInt();
        float delay = state.UnsafePopAsFloat();

        // TODO: It'd be nicer if we could use something such as DreamThread.Spawn here
        // and have state.Spawn return a ProcState instead
        DreamThread newContext = state.Spawn();

        //Negative delays mean the spawned code runs immediately
        if (delay < 0) {
            newContext.Resume().Dispose();
            // TODO: Does the rest of the proc get scheduled?
            // Does the value of the delay mean anything?
        } else {
            async void Wait() {
                await state.ProcScheduler.CreateDelay(delay);
                newContext.Resume().Dispose();
            }

            Wait();
        }

        state.Jump(jumpTo);
        return ProcStatus.Continue;
    }

    public static ProcStatus DebuggerBreakpoint(DMProcState state) {
        return state.DebugManager.HandleBreakpoint(state);
    }

    public static ProcStatus ReturnReferenceValue(DMProcState state) {
        DreamReference reference = state.ReadReference();
        using DreamValue value = state.GetReferenceValue(reference);

        state.SetReturn(value);
        return ProcStatus.Returned;
    }

    #endregion Flow

    #region Builtins

    public static ProcStatus GetStep(DMProcState state) {
        using DreamValue d = state.Pop();
        using DreamValue l = state.Pop();

        if (!l.TryGetValueAsDreamObject<DreamObjectAtom>(out DreamObjectAtom? loc)) {
            state.Push(DreamValue.Null);
            return ProcStatus.Continue;
        }

        if (!d.TryGetValueAsInteger(out int dir) && !d.IsNull) {
            state.Push(DreamValue.Null);
            return ProcStatus.Continue;
        }

        state.Push(new DreamValue(DreamProcNativeHelpers.GetStep(state.Proc.AtomManager, state.Proc.DreamMapManager,
            loc, (AtomDirection)dir)));
        return ProcStatus.Continue;
    }

    public static ProcStatus Length(DMProcState state) {
        using DreamValue o = state.Pop();

        state.Push(DreamProcNativeRoot._length(o, true));
        return ProcStatus.Continue;
    }

    public static ProcStatus GetDir(DMProcState state) {
        using DreamValue loc2R = state.Pop();
        using DreamValue loc1R = state.Pop();

        if (!loc1R.TryGetValueAsDreamObject<DreamObjectAtom>(out DreamObjectAtom? loc1)) {
            state.Push(new DreamValue(0));
            return ProcStatus.Continue;
        }

        if (!loc2R.TryGetValueAsDreamObject<DreamObjectAtom>(out DreamObjectAtom? loc2)) {
            state.Push(new DreamValue(0));
            return ProcStatus.Continue;
        }

        state.Push(new DreamValue((int)DreamProcNativeHelpers.GetDir(state.Proc.AtomManager, loc1, loc2)));
        return ProcStatus.Continue;
    }

    public static ProcStatus Gradient(DMProcState state) {
        DMProcState.DMStackArgumentInfo argumentInfo = state.ReadProcArguments();

        DreamValue gradientIndex = default, gradientColorSpace = DreamValue.Null;
        List<DreamValue> gradientValues = new();

        switch (argumentInfo.Type)
        {
            // Arguments need specially handled due to the fact that index can be either a keyed arg or the last arg
            // This is kinda ridiculous...
            case DMCallArgumentsType.FromStackKeyed:
            {
                ReadOnlySpan<DreamValue> stack = state.PopCount(argumentInfo.StackSize);
                int argumentCount = argumentInfo.StackSize / 2;

                gradientValues.EnsureCapacity(argumentCount - 1);
                for (var i = 0; i < argumentCount; i++) {
                    using DreamValue argumentKey = stack[i * 2];
                    using DreamValue argumentValue = stack[i * 2 + 1];

                    if (argumentKey.TryGetValueAsString(out string? argumentKeyStr)) {
                        switch (argumentKeyStr)
                        {
                            case "index":
                                gradientIndex = argumentValue;
                                continue;
                            case "space":
                                gradientColorSpace = argumentValue;
                                continue;
                        }
                    }

                    if (i == argumentCount - 1 && gradientIndex == default) {
                        gradientIndex = argumentValue;
                        continue;
                    }

                    gradientValues.Add(argumentValue);
                }

                break;
            }
            case DMCallArgumentsType.FromArgumentList:
            {
                using DreamValue argListStack = state.Pop();
                if (!argListStack.TryGetValueAsDreamList(out DreamList? argList))
                    throw new DMException("Invalid gradient() arguments");

                List<DreamValue> argListValues = argList.GetValues();

                gradientValues.EnsureCapacity(argListValues.Count - 1);
                for (var i = 0; i < argListValues.Count; i++) {
                    DreamValue value = argListValues[i];

                    if (value.TryGetValueAsString(out string? argumentKey)) {
                        switch (argumentKey)
                        {
                            case "index":
                                gradientIndex = argList.GetValue(value);
                                continue;
                            case "space":
                                gradientColorSpace = argList.GetValue(value);
                                continue;
                        }
                    }

                    if (i == argListValues.Count - 1 && gradientIndex == default) {
                        gradientIndex = value;
                        continue;
                    }

                    gradientValues.Add(value);
                }

                break;
            }
            default:
            {
                using DreamProcArguments arguments = state.PopProcArguments(null, argumentInfo);

                gradientIndex = arguments.Values[^1];
                for (var i = 0; i < arguments.Count - 1; i++) gradientValues.Add(arguments.Values[i]);
                break;
            }
        }

        if (gradientIndex == default)
            throw new DMException("No gradient index given");

        state.Push(CalculateGradient(gradientValues, gradientColorSpace, gradientIndex));
        gradientColorSpace.Dispose();
        gradientIndex.Dispose();
        return ProcStatus.Continue;
    }

    public static ProcStatus Rgb(DMProcState state) {
        DMProcState.DMStackArgumentInfo argumentInfo = state.ReadProcArguments();
        using var arguments = new DMProcState.DMStackArguments(state, argumentInfo);

        (DreamValue Key, DreamValue Value)[] argumentsArray = arguments.ToArray();
        if (argumentsArray.Length is < 3 or > 5)
            throw new DMException("Expected 3 to 5 arguments for rgb()");

        var values = new (string?, float?)[arguments.Count];
        for (var i = 0; i < argumentsArray.Length; i++) {
            (DreamValue Key, DreamValue Value) arg = argumentsArray[i];
            float argValue = arg.Value.UnsafeGetValueAsFloat();

            if (arg.Key.TryGetValueAsString(out string? argName))
                values[i] = (argName, argValue);
            else
                values[i] = (null, argValue);
        }

        string result = SharedOperations.ParseRgb(values);
        state.Push(new DreamValue(result));
        return ProcStatus.Continue;
    }

    /* vars:

    animate smoothly:

    alpha
    color
    glide_size
    infra_luminosity
    layer
    maptext_width, maptext_height, maptext_x, maptext_y
    luminosity
    pixel_x, pixel_y, pixel_w, pixel_z
    transform

    do not animate smoothly:

    dir
    icon
    icon_state
    invisibility
    maptext
    suffix

    */
    public static ProcStatus Animate(DMProcState state) {
        DMProcState.DMStackArgumentInfo argumentInfo = state.ReadProcArguments();
        using var callArguments = new DMProcState.DMStackArguments(state, argumentInfo);
        var arguments = new Dictionary<string, DreamValue>();

        if (callArguments.Count == 0) {
            state.Push(DreamValue.Null);
            return ProcStatus.Continue;
        }

        try {
            (DreamValue Key, DreamValue Value)[] argumentsArray = callArguments.ToArray();
            foreach ((DreamValue name, DreamValue value) in argumentsArray) {
                if (!name.TryGetValueAsString(out string? argName))
                    continue;

                value.IncRef();
                arguments[argName] = value;
            }

            if (!arguments.TryGetValue("Object", out DreamValue objArg) && argumentsArray[0].Key == DreamValue.Null)
                objArg = argumentsArray[0].Value;

            var chainAnim = false;

            if (!objArg.TryGetValueAsDreamObject<DreamObject>(out DreamObject? obj)) {
                if (state.Thread.LastAnimatedObject is null || state.Thread.LastAnimatedObject.Value.IsNull) {
                    throw new DMException("animate() called without an object and no previous object to animate");
                } else if (!state.Thread.LastAnimatedObject.Value.TryGetValueAsDreamObject<DreamObject>(out obj)) {
                    state.Push(DreamValue.Null);
                    return ProcStatus.Continue;
                }

                chainAnim = true;
            }

            state.Thread.LastAnimatedObject = new DreamValue(obj);
            if (obj.IsSubtypeOf(state.Proc.ObjectTree.Filter)) { //TODO animate filters
                state.Push(DreamValue.Null);
                return ProcStatus.Continue;
            }

            arguments.TryGetValue("time", out DreamValue timeArg);
            arguments.TryGetValue("loop", out DreamValue loopArg);
            arguments.TryGetValue("easing", out DreamValue easingArg);
            arguments.TryGetValue("flags", out DreamValue flagsArg);
            arguments.TryGetValue("delay", out DreamValue delayArg);
            loopArg.TryGetValueAsInteger(out int loop);
            easingArg.TryGetValueAsInteger(out int easing);
            flagsArg.TryGetValueAsInteger(out int flagsInt);
            delayArg.TryGetValueAsInteger(out int delay);

            if (!timeArg.TryGetValueAsFloat(out float time))
                // A non-number time arg results in the animation happening instantly
                time = 0f;

            var flags = (AnimationFlags)flagsInt;
            if ((flags & (AnimationFlags.AnimationParallel | AnimationFlags.AnimationContinue)) != 0)
                chainAnim = true;
            if ((flags & AnimationFlags.AnimationEndNow) != 0)
                chainAnim = false;

            AtomManager atomManager = state.Proc.AtomManager;
            TimeSpan duration = TimeSpan.FromMilliseconds(time * 100);
            atomManager.AnimateAppearance(obj, duration, (AnimationEasing)easing, loop, flags, delay, chainAnim,
                appearance => {
                    bool isRelative = flags.HasFlag(AnimationFlags.AnimationRelative);

                    foreach (KeyValuePair<string, DreamValue> arg in arguments) {
                        if (!atomManager.IsValidAppearanceVar(arg.Key))
                            continue;

                        DreamValue animateValue = arg.Value;

                        if (isRelative)
                            switch (arg.Key) {
                                case "transform":
                                    if (!animateValue.TryGetValueAsDreamObject<DreamObjectMatrix>(
                                            out DreamObjectMatrix? multTransform))
                                        break;

                                    var objTransformClone =
                                        DreamObjectMatrix.MakeMatrix(state.Proc.ObjectTree, appearance.Transform);

                                    DreamObjectMatrix.MultiplyMatrix(objTransformClone, multTransform);
                                    animateValue = new DreamValue(objTransformClone);
                                    break;
                                case "color": {
                                    ColorMatrix cMatrix;
                                    if (animateValue.TryGetValueAsString(out string? colorStr) &&
                                        Color.TryParse(colorStr, out Color colorObj))
                                        cMatrix = new ColorMatrix(colorObj);
                                    else if (!animateValue.TryGetValueAsDreamList(out DreamList? colorList) ||
                                             !DreamProcNativeHelpers.TryParseColorMatrix(colorList, out cMatrix))
                                        cMatrix = ColorMatrix.Identity; //fallback to identity if invalid

                                    ColorMatrix objCMatrix;
                                    using DreamValue objColor = obj.GetVariable("color");
                                    if (objColor.TryGetValueAsString(out string? objColorStr) &&
                                        Color.TryParse(objColorStr, out Color objColorObj))
                                        objCMatrix = new ColorMatrix(objColorObj);
                                    else if (!objColor.TryGetValueAsDreamList(out DreamList? objColorList) ||
                                             !DreamProcNativeHelpers.TryParseColorMatrix(objColorList, out objCMatrix))
                                        objCMatrix = ColorMatrix.Identity; //fallback to identity if invalid

                                    ColorMatrix.Multiply(ref objCMatrix, ref cMatrix, out ColorMatrix resultMatrix);
                                    animateValue = new DreamValue(new DreamList(
                                        state.Proc.ObjectTree.List.ObjectDefinition,
                                        resultMatrix.GetValues().Select(x => new DreamValue(x)).ToList(), null));
                                    break;
                                }
                                case "pixel_x":
                                case "pixel_y":
                                case "pixel_z":
                                case "pixel_w":
                                case "maptext_width":
                                case "maptext_height":
                                case "maptext_x":
                                case "maptext_y":
                                case "luminosity":
                                case "layer":
                                case "alpha": {
                                    using DreamValue originalValue = atomManager.GetAppearanceVar(appearance, arg.Key);
                                    float originalFloat = originalValue.UnsafeGetValueAsFloat();

                                    animateValue = new DreamValue(animateValue.UnsafeGetValueAsFloat() + originalFloat);
                                    break;
                                }
                            }

                        obj.SetVariableValue(arg.Key, animateValue);
                        atomManager.SetAppearanceVar(appearance, arg.Key, animateValue);
                    }
                });

            state.Push(DreamValue.Null);
            return ProcStatus.Continue;
        }
        finally {
            foreach (DreamValue argument in arguments.Values)
                argument.Dispose();
        }
    }

    public static ProcStatus LocateCoord(DMProcState state) {
        var z = (int)state.UnsafePopAsFloat();
        var y = (int)state.UnsafePopAsFloat();
        var x = (int)state.UnsafePopAsFloat();

        state.Proc.DreamMapManager.TryGetTurfAt((x, y), z, out DreamObjectTurf? turf);
        state.Push(new DreamValue(turf));
        return ProcStatus.Continue;
    }

    public static ProcStatus Locate(DMProcState state) {
        using DreamValue containerStack = state.Pop();
        if (!containerStack.TryGetValueAsDreamObject(out DreamObject? container)) {
            state.Push(DreamValue.Null);
            return ProcStatus.Continue;
        }

        using DreamValue value = state.Pop();

        // Enumerate atoms rather than creating a list of every /atom using WorldContentsList.GetValues()
        if (container is DreamObjectWorld && value.Type != DreamValue.DreamValueType.String) {
            // "locate(value) in world" only works on type paths. Other values return null.
            if (value.TryGetValueAsType(out TreeEntry? searchingType)) {
                DreamObjectAtom? result = state.Proc.AtomManager.EnumerateAtoms(searchingType).FirstOrDefault();

                state.Push(new DreamValue(result));
            } else {
                state.Push(DreamValue.Null);
            }

            return ProcStatus.Continue;
        }

        DreamList? containerList;
        if (container is DreamObjectAtom) {
            using DreamValue contents = container.GetVariable("contents");

            contents.TryGetValueAsDreamList(out containerList);
        } else {
            containerList = container as DreamList;
        }

        if (value.TryGetValueAsString(out string? refString)) {
            using DreamValue refValue = state.Proc.RefManager.LocateRef(refString);

            if (container is not DreamObjectWorld &&
                containerList is not null) //if it's a valid ref, it's in world, we don't need to check
                state.Push(containerList.ContainsValue(refValue) ? refValue : DreamValue.Null);
            else
                state.Push(refValue);
        } else if (value.TryGetValueAsType(out TreeEntry? ancestor)) {
            if (containerList == null) {
                state.Push(DreamValue.Null);

                return ProcStatus.Continue;
            }

            foreach (DreamValue containerItem in containerList.GetValues()) {
                DreamObjectDefinition itemDef;
                if (containerItem.TryGetValueAsType(out TreeEntry? type))
                    itemDef = type.ObjectDefinition;
                else if (containerItem.TryGetValueAsDreamObject(out DreamObject? dmObject) && dmObject != null)
                    itemDef = dmObject.ObjectDefinition;
                else
                    continue;

                if (itemDef.IsSubtypeOf(ancestor)) {
                    state.Push(containerItem);

                    return ProcStatus.Continue;
                }
            }

            state.Push(DreamValue.Null);
        } else {
            if (containerList == null) {
                state.Push(DreamValue.Null);

                return ProcStatus.Continue;
            }

            state.Push(containerList.ContainsValue(value) ? value : DreamValue.Null);
        }

        return ProcStatus.Continue;
    }

    public static ProcStatus PickWeighted(DMProcState state) {
        int count = state.ReadInt();

        (DreamValue Value, float CumulativeWeight)[] values = new (DreamValue, float)[count];
        float totalWeight = 0;
        for (var i = 0; i < count; i++) {
            using DreamValue value = state.Pop();
            using DreamValue weightStack = state.Pop();
            if (!weightStack.TryGetValueAsFloat(out float weight))
                weight = 100;

            totalWeight += weight;
            values[i] = (value, totalWeight);
        }

        double pick = state.DreamManager.Random.NextDouble() * totalWeight;
        for (var i = 0; i < values.Length; i++)
            if (pick < values[i].CumulativeWeight) {
                state.Push(values[i].Value);
                break;
            }

        return ProcStatus.Continue;
    }

    public static ProcStatus PickUnweighted(DMProcState state) {
        int count = state.ReadInt();

        if (count == 1) {
            using DreamValue value = state.Pop();

            List<DreamValue> values;
            if (value.TryGetValueAsDreamList(out DreamList? list)) {
                values = list.GetValues();
            } else {
                state.Push(value);
                return ProcStatus.Continue;
            }

            if (values.Count == 0)
                throw new DMException("pick() from empty list");

            state.Push(values[state.DreamManager.Random.Next(0, values.Count)]);
        } else {
            int pickedIndex = state.DreamManager.Random.Next(0, count);
            ReadOnlySpan<DreamValue> possibleValues = state.PopCount(count);

            // the way we're going about this is very important
            using DreamValue value = possibleValues[pickedIndex];
            value.IncRef();

            foreach (DreamValue dropped in possibleValues)
                dropped.Dispose();
            state.Push(value);
        }

        return ProcStatus.Continue;
    }

    public static ProcStatus Prob(DMProcState state) {
        using DreamValue probability = state.Pop();

        if (probability.TryGetValueAsFloat(out float probabilityValue)) {
            int result = state.DreamManager.Random.Prob(probabilityValue / 100) ? 1 : 0;

            state.Push(new DreamValue(result));
        } else {
            state.Push(new DreamValue(0));
        }

        return ProcStatus.Continue;
    }

    public static ProcStatus IsSaved(DMProcState state) {
        using DreamValue key = state.Pop();
        using DreamValue owner = state.Pop();

        // number indices always evaluate to false here
        if (key.TryGetValueAsFloat(out _)) {
            state.Push(DreamValue.False);
            return ProcStatus.Continue;
        }

        if (!key.TryGetValueAsString(out string? property))
            throw new DMException($"Invalid var for issaved() call: {key}");

        if (owner.TryGetValueAsDreamObject(out DreamObject dreamObject)) {
            state.Push(dreamObject.IsSaved(property) ? DreamValue.True : DreamValue.False);
            return ProcStatus.Continue;
        }

        DreamObjectDefinition objectDefinition;
        if (owner.TryGetValueAsDreamObject(out DreamObject? dreamObject2))
            objectDefinition = dreamObject2.ObjectDefinition;
        else if (owner.TryGetValueAsType(out TreeEntry? type))
            objectDefinition = type.ObjectDefinition;
        else
            throw new DMException($"Invalid owner for issaved() call {owner}");

        if (objectDefinition.GlobalVariables.ContainsKey(property)
            || (objectDefinition.ConstVariables is not null && objectDefinition.ConstVariables.Contains(property))
            || (objectDefinition.TmpVariables is not null && objectDefinition.TmpVariables.Contains(property)))
            state.Push(new DreamValue(0));
        else
            state.Push(new DreamValue(1));

        return ProcStatus.Continue;
    }

    #endregion Builtins

    #region Others

    private static void PerformOutput(DreamValue a, DreamValue b) {
        if (a.TryGetValueAsDreamResource(out DreamResource? resource)) {
            resource.Output(b);
        } else if (a.TryGetValueAsDreamObject(out DreamObject? dreamObject)) {
            if (dreamObject == null)
                return;

            dreamObject.OperatorOutput(b);
        } else if (a.TryGetValueAsFloatCoerceNull(out _)) { // no-op
            // TODO: When we have runtime pragmas we should probably emit a no-op runtime. They probably meant to do <<= not <<
        } else {
            throw new NotImplementedException($"Unimplemented output operation between {a} and {b}");
        }
    }

    public static ProcStatus OutputReference(DMProcState state) {
        DreamReference leftRef = state.ReadReference();
        using DreamValue right = state.Pop();

        if (leftRef.Type == DMReference.Type.ListIndex) {
            state.GetIndexReferenceValues(leftRef, out _, out DreamValue indexing, true);

            if (indexing.TryGetValueAsDreamObject<DreamObjectSavefile>(out _)) {
                // Savefiles get some special treatment.
                // "savefile[A] << B" is the same as "savefile[A] = B"

                state.AssignReference(leftRef, right);
                return ProcStatus.Continue;
            }
        }

        using DreamValue value = state.GetReferenceValue(leftRef);
        PerformOutput(value, right);
        return ProcStatus.Continue;
    }

    public static ProcStatus Output(DMProcState state) {
        using DreamValue right = state.Pop();
        using DreamValue left = state.Pop();

        PerformOutput(left, right);
        return ProcStatus.Continue;
    }

    public static ProcStatus Input(DMProcState state) {
        DreamReference leftRef = state.ReadReference();
        DreamReference rightRef = state.ReadReference();

        if (leftRef.Type == DMReference.Type.ListIndex) {
            state.GetIndexReferenceValues(leftRef, out _, out DreamValue indexing, true);

            if (indexing.TryGetValueAsDreamObject<DreamObjectSavefile>(out _)) {
                // Savefiles get some special treatment.
                // "savefile[A] >> B" is the same as "B = savefile[A]"
                state.AssignReference(rightRef, state.GetReferenceValue(leftRef));
                return ProcStatus.Continue;
            }

            // Pop the reference's stack values
            state.PopReference(leftRef);
            state.PopReference(rightRef);
        } else {
            using DreamValue leftValue = state.GetReferenceValue(leftRef);

            if (leftValue.TryGetValueAsDreamObject<DreamObjectSavefile>(out DreamObjectSavefile? savefile)) {
                // Savefiles get some special treatment.
                // "savefile >> B" is the same as "B = savefile[current_dir]"
                using DreamValue result = savefile.OperatorInput();

                state.AssignReference(rightRef, result);
                return ProcStatus.Continue;
            }
        }

        throw new NotImplementedException($"Input operation is unimplemented for {leftRef} and {rightRef}");
    }

    public static ProcStatus Browse(DMProcState state) {
        using DreamValue optionsStack = state.Pop();
        using DreamValue bodyStack = state.Pop();
        using DreamValue receiverStack = state.Pop();

        optionsStack.TryGetValueAsString(out string? options);
        if (!receiverStack.TryGetValueAsDreamObject<DreamObject>(out DreamObject? receiver))
            return ProcStatus.Continue;

        IEnumerable<DreamConnection> clients;
        switch (receiver)
        {
            case DreamObjectMob {Connection: { } mobConnection}:
                clients = new[] {mobConnection};
                break;
            case DreamObjectClient receiverClient:
                clients = new[] {receiverClient.Connection};
                break;
            default: {
                if (receiver == state.DreamManager.WorldInstance)
                    clients = state.DreamManager.Connections;
                else
                    throw new DMException($"Invalid browse() recipient: expected mob, client, or world, got {receiver}");
                break;
            }
        }

        string? browseValue;
        if (bodyStack.TryGetValueAsDreamResource(out DreamResource? resource)) {
            browseValue = resource.ReadAsString();
        } else if (bodyStack.TryGetValueAsString(out browseValue) || bodyStack.IsNull) {
            // Got it.
        } else {
            throw new DMException($"Invalid browse() body: expected resource or string, got {bodyStack}");
        }

        foreach (DreamConnection client in clients) client.Browse(browseValue, options);

        return ProcStatus.Continue;
    }

    public static ProcStatus BrowseResource(DMProcState state) {
        using DreamValue filename = state.Pop();
        using DreamValue value = state.Pop();
        using DreamValue receiverStack = state.Pop();

        if (!value.TryGetValueAsDreamResource(out DreamResource? file)) {
            if (state.Proc.DreamResourceManager.TryLoadIcon(value, out IconResource? icon))
                file = icon;
            else
                throw new NotImplementedException();
        }

        if (!receiverStack.TryGetValueAsDreamObject<DreamObject>(out DreamObject? receiver))
            return ProcStatus.Continue;

        DreamConnection? connection;
        switch (receiver)
        {
            case DreamObjectMob receiverMob:
                connection = receiverMob.Connection;
                break;
            case DreamObjectClient receiverClient:
                connection = receiverClient.Connection;
                break;
            default:
                throw new DMException("Invalid browse_rsc() recipient");
        }

        connection?.BrowseResource(file,
            filename.IsNull ? Path.GetFileName(file.ResourcePath) : filename.GetValueAsString());
        return ProcStatus.Continue;
    }

    public static ProcStatus DeleteObject(DMProcState state) {
        using DreamValue dreamObjectStack = state.Pop();

        if (dreamObjectStack.TryGetValueAsDreamObject<DreamObject>(out DreamObject? dreamObject)) {
            dreamObject.Delete();

            if (dreamObject ==
                state.Instance) // We just deleted our src, end the proc TODO: Is the entire thread cancelled?
                return ProcStatus.Returned;
        }

        return ProcStatus.Continue;
    }

    public static ProcStatus OutputControl(DMProcState state) {
        using DreamValue controlStack = state.Pop();
        using DreamValue messageStack = state.Pop();
        using DreamValue receiverStack = state.Pop();
        string control = controlStack.GetValueAsString();
        string message = messageStack.Stringify();
        if (!receiverStack.TryGetValueAsDreamObject<DreamObject>(out DreamObject? receiver))
            return ProcStatus.Continue;

        // TODO: When errors are more strict (or a setting for it added), a null receiver should error

        switch (receiver)
        {
            case DreamObjectMob receiverMob:
                receiverMob.Connection?.OutputControl(message, control);
                break;
            case DreamObjectClient receiverClient:
                receiverClient.Connection.OutputControl(message, control);
                break;
            case DreamObjectWorld: {
                // Output to every player
                foreach (DreamConnection connection in state.DreamManager.Connections)
                    connection.OutputControl(message, control);
                break;
            }
            case DreamList list: {
                // Output to every mob in the left-hand list.
                foreach (DreamValue entry in list.GetValues())
                    if (entry.TryGetValueAsDreamObject(out DreamObject? entryObj)) {
                        switch (entryObj)
                        {
                            case DreamObjectMob entryMob:
                                entryMob.Connection?.OutputControl(message, control);
                                break;
                            case DreamObjectClient entryClient:
                                entryClient.Connection.OutputControl(message, control);
                                break;
                        }
                    }

                break;
            }
            default:
                // TODO: BYOND's behavior is to ignore rather than throw here
                throw new DMException($"Invalid output() recipient: {receiver}");
        }

        return ProcStatus.Continue;
    }

    public static ProcStatus Prompt(DMProcState state) {
        var types = (DreamValueType)state.ReadInt();
        using DreamValue list = state.Pop();
        DreamValue message, title, defaultValue;

        using DreamValue arg1 = state.Pop();
        using DreamValue arg2 = state.Pop();
        using DreamValue arg3 = state.Pop();
        using DreamValue arg4 = state.Pop();
        arg1.TryGetValueAsDreamObject(out DreamObject? recipient);

        if (recipient is DreamObjectMob or DreamObjectClient) {
            message = arg2;
            title = arg3;
            defaultValue = arg4;
        } else {
            recipient = state.Usr;
            message = arg1;
            title = arg2;
            defaultValue = arg3;
            // arg4 isn't used, and should be null
        }

        DreamConnection? connection = null;
        switch (recipient)
        {
            case DreamObjectMob recipientMob:
                connection = recipientMob.Connection;
                break;
            case DreamObjectClient recipientClient:
                connection = recipientClient.Connection;
                break;
        }

        if (connection == null) {
            state.Push(DreamValue.Null);
            return ProcStatus.Continue;
        }

        Task<DreamValue> promptTask;
        if (list.TryGetValueAsDreamList(out DreamList? valueList))
            promptTask = connection.PromptList(types, valueList, title.Stringify(), message.Stringify(), defaultValue);
        else
            promptTask = connection.Prompt(types, title.Stringify(), message.Stringify(), defaultValue.Stringify());

        // Could use a better solution. Either no anonymous async native proc at all, or just a better way to call them.
        ProcState waiter = AsyncNativeProc.CreateAnonymousState(state.Thread, async _ => await promptTask);
        state.Thread.PushProcState(waiter);
        return ProcStatus.Called;
    }

    public static ProcStatus Link(DMProcState state) {
        using DreamValue url = state.Pop();
        using DreamValue receiverStack = state.Pop();
        if (!receiverStack.TryGetValueAsDreamObject<DreamObject>(out DreamObject? receiver))
            return ProcStatus.Continue;

        DreamConnection? connection = receiver switch {
            DreamObjectMob receiverMob => receiverMob.Connection,
            DreamObjectClient receiverClient => receiverClient.Connection,
            _ => throw new DMException("Invalid link() recipient")
        };

        if (!url.TryGetValueAsString(out string? urlStr)) throw new DMException($"Invalid link() url: {url}");

        if (string.IsNullOrWhiteSpace(urlStr)) return ProcStatus.Continue;

        connection?.SendLink(urlStr);
        return ProcStatus.Continue;
    }

    public static ProcStatus Ftp(DMProcState state) {
        using DreamValue name = state.Pop();
        using DreamValue file = state.Pop();
        using DreamValue receiverStack = state.Pop();
        if (!receiverStack.TryGetValueAsDreamObject<DreamObject>(out DreamObject? receiver))
            return ProcStatus.Continue;

        DreamConnection? connection;
        switch (receiver)
        {
            case DreamObjectMob receiverMob:
                connection = receiverMob.Connection;
                break;
            case DreamObjectClient receiverClient:
                connection = receiverClient.Connection;
                break;
            default:
                throw new DMException("Invalid ftp() recipient");
        }

        if (!file.TryGetValueAsDreamResource(out DreamResource? resource)) {
            if (file.TryGetValueAsString(out string? resourcePath)) {
                if (!state.Proc.DreamResourceManager.DoesFileExist(resourcePath))
                    return ProcStatus.Continue; // Do nothing

                resource = state.Proc.DreamResourceManager.LoadResource(resourcePath);
            } else if (file.TryGetValueAsDreamObject<DreamObjectIcon>(out DreamObjectIcon? icon)) {
                resource = icon.Icon.GenerateDMI();
            } else {
                throw new DMException($"{file} is not a valid file");
            }
        }

        if (!name.TryGetValueAsString(out string? suggestedName))
            suggestedName = Path.GetFileName(resource.ResourcePath) ?? string.Empty;

        connection.SendFile(resource, suggestedName);
        return ProcStatus.Continue;
    }

    /// <summary>
    ///     Right now this is used exclusively by addtext() calls, to concatenate its arguments together,
    ///     but later it might make sense to have this be a simplification path for detected repetitive additions of strings,
    ///     so as to slightly reduce the amount of re-allocation taking place.
    /// </summary>
    /// .
    public static ProcStatus MassConcatenation(DMProcState state) {
        int count = state.ReadInt();

        // One or zero arguments -- shouldn't really ever happen. addtext() compiletimes with <2 args and stringification should probably be a different opcode
        if (count < 2) {
            // TODO: tweak this warning if this ever gets used for other sorts of string concat
            Logger.GetSawmill("opendream.opcodes").Warning($"addtext() called with {count} arguments at runtime.");
            state.Push(DreamValue.Null);
            return ProcStatus.Continue;
        }

        // An approximate guess at how big this string is going to be.
        int estimatedStringSize = count * 10; // FIXME: We can do better with string size prediction here.
        var builder = new StringBuilder(estimatedStringSize);

        foreach (DreamValue add in state.PopCount(count)) {
            if (add.TryGetValueAsString(out string? addStr)) builder.Append(addStr);

            add.Dispose();
        }

        state.Push(new DreamValue(builder.ToString()));
        return ProcStatus.Continue;
    }

    public static ProcStatus DereferenceIndex(DMProcState state) {
        using DreamValue index = state.Pop();
        using DreamValue obj = state.Pop();
        using DreamValue indexResult = state.GetIndex(obj, index, state);

        state.Push(indexResult);
        return ProcStatus.Continue;
    }

    public static ProcStatus IndexRefWithString(DMProcState state) {
        DreamReference reference = state.ReadReference();
        using DreamValue refValue = state.GetReferenceValue(reference);

        var index = new DreamValue(state.ReadString());
        using DreamValue indexResult = state.GetIndex(refValue, index, state);

        state.Push(indexResult);
        return ProcStatus.Continue;
    }

    public static ProcStatus DereferenceCall(DMProcState state) {
        string name = state.ReadString();
        DMProcState.DMStackArgumentInfo argumentInfo = state.ReadProcArguments();
        using var arguments = new DMProcState.DMStackArguments(state, argumentInfo);
        using DreamValue obj = state.Pop();

        if (!obj.TryGetValueAsDreamObject(out DreamObject? instance) || instance == null)
            throw new DMException($"Cannot dereference proc \"{name}\" from {obj}");
        if (!instance.TryGetProc(name, out DreamProc? proc))
            throw new DMException($"Type {instance.ObjectDefinition.Type} has no proc called \"{name}\"");

        return state.Call(proc, instance, arguments.ToProcArguments(proc));
    }

    #endregion Others

    #region Helpers

    public static bool IsEqual(DreamValue first, DreamValue second) {
        if (first.Type != second.Type)
            return false;

        switch (first.Type) {
            case DreamValue.DreamValueType.DreamObject: {
                DreamObject? firstValue = first.MustGetValueAsDreamObject();

                return firstValue == second.MustGetValueAsDreamObject();
            }
            case DreamValue.DreamValueType.Float: {
                float firstValue = first.MustGetValueAsFloat();

                // ReSharper disable once CompareOfFloatsByEqualityOperator
                return firstValue == second.MustGetValueAsFloat();
            }
            case DreamValue.DreamValueType.String: {
                string firstValue = first.MustGetValueAsString();

                return firstValue == second.MustGetValueAsString();
            }
            case DreamValue.DreamValueType.DreamType: {
                TreeEntry firstValue = first.MustGetValueAsType();

                return firstValue.Equals(second.MustGetValueAsType());
            }
            case DreamValue.DreamValueType.DreamProc:
                return first.MustGetValueAsProc() == second.MustGetValueAsProc();
            case DreamValue.DreamValueType.DreamResource: {
                DreamResource firstValue = first.MustGetValueAsDreamResource();

                return firstValue.ResourcePath == second.MustGetValueAsDreamResource().ResourcePath;
            }
            case DreamValue.DreamValueType.Appearance: {
                MutableAppearance firstValue = first.MustGetValueAsAppearance();

                return firstValue.Equals(second.MustGetValueAsAppearance());
            }
        }

        throw new NotImplementedException($"Equal comparison for {first} and {second} is not implemented");
    }

    private static bool IsEquivalent(DreamValue first, DreamValue second) {
        if (first.TryGetValueAsDreamObject<DreamObject>(out DreamObject? firstObject)) {
            using DreamValue opResult = firstObject.OperatorEquivalent(second);

            return opResult.IsTruthy();
        }

        // Behaviour is otherwise equivalent (pun intended) to ==
        return IsEqual(first, second);
    }

    private static bool TryGetReferenceComparisonResult(DreamValue first, DreamValue second,
        out DreamValue result) {
        // BYOND leaves the left operand untouched whenever the right operand is reference-like regardless of the left operand's runtime representation
        // The reverse is a runtime error: reference <op> null
        if (!second.IsNull && second.Type != DreamValue.DreamValueType.Float &&
            second.Type != DreamValue.DreamValueType.String) {
            result = first;
            return true;
        }

        result = default;
        return false;
    }

    private static bool IsGreaterThan(DreamValue first, DreamValue second) {
        switch (first.Type) {
            case DreamValue.DreamValueType.Float when second.Type == DreamValue.DreamValueType.Float:
                return first.MustGetValueAsFloat() > second.MustGetValueAsFloat();
            case DreamValue.DreamValueType.Float when second.IsNull:
                return first.MustGetValueAsFloat() > 0;
            case DreamValue.DreamValueType.String when second.Type == DreamValue.DreamValueType.String:
                return string.Compare(first.MustGetValueAsString(), second.MustGetValueAsString(),
                    StringComparison.Ordinal) > 0;
            default: {
                if (first.IsNull) {
                    if (second.Type == DreamValue.DreamValueType.Float) return 0 > second.MustGetValueAsFloat();
                    if (second.TryGetValueAsString(out _)) return false;
                    if (second.IsNull) return false;
                }

                throw new DMException("Invalid greater than comparison on " + first + " and " + second);
            }
        }
    }

    private static bool IsLessThan(DreamValue first, DreamValue second) {
        switch (first.Type) {
            case DreamValue.DreamValueType.Float when second.Type == DreamValue.DreamValueType.Float:
                return first.MustGetValueAsFloat() < second.MustGetValueAsFloat();
            case DreamValue.DreamValueType.Float when second.IsNull:
                return first.MustGetValueAsFloat() < 0;
            case DreamValue.DreamValueType.String when second.Type == DreamValue.DreamValueType.String:
                return string.Compare(first.MustGetValueAsString(), second.MustGetValueAsString(),
                    StringComparison.Ordinal) < 0;
            default: {
                if (first.IsNull) {
                    if (second.Type == DreamValue.DreamValueType.Float) return 0 < second.MustGetValueAsFloat();
                    if (second.TryGetValueAsString(out string? s)) return s != "";
                    if (second.IsNull) return false;
                }

                throw new DMException("Invalid less than comparison between " + first + " and " + second);
            }
        }
    }

    [MustDisposeResource]
    private static DreamValue BitXorValues(DreamObjectTree objectTree, DreamValue first, DreamValue second) {
        if (first.TryGetValueAsDreamList(out DreamList? list)) {
            DreamList newList = objectTree.CreateList();
            Dictionary<DreamValue, DreamValue> firstAssociations = list.GetAssociativeValues();

            void AddValue(DreamValue value, Dictionary<DreamValue, DreamValue>? associations) {
                newList.AddValue(value);
                if (associations?.TryGetValue(value, out DreamValue associatedValue) is true)
                    newList.SetValue(value, associatedValue);
            }

            if (second.TryGetValueAsDreamList(out DreamList? secondList)) {
                Dictionary<DreamValue, DreamValue> secondAssociations = secondList.GetAssociativeValues();
                foreach (DreamValue value in list.EnumerateValues())
                    if (!secondList.ContainsValue(value))
                        AddValue(value, firstAssociations);

                foreach (DreamValue value in secondList.EnumerateValues())
                    if (!list.ContainsValue(value))
                        AddValue(value, secondAssociations);
            } else {
                bool secondInList = list.ContainsValue(second);
                foreach (DreamValue value in list.EnumerateValues())
                    if (value != second)
                        AddValue(value, firstAssociations);

                if (!secondInList) newList.AddValue(second);
            }

            return new DreamValue(newList);
        }

        switch (first.Type) {
            case DreamValue.DreamValueType.Float when second.Type == DreamValue.DreamValueType.Float:
                return new DreamValue(first.MustGetValueAsInteger() ^ second.MustGetValueAsInteger());
            case DreamValue.DreamValueType.DreamObject when first.IsNull && second.IsNull:
                return DreamValue.Null;
            case DreamValue.DreamValueType.DreamObject
                when first.IsNull && second.Type == DreamValue.DreamValueType.Float:
                return new DreamValue(second.MustGetValueAsInteger());
            case DreamValue.DreamValueType.Float when second.IsNull:
                return new DreamValue(first.MustGetValueAsInteger());
            default:
                throw new DMException($"Invalid xor operation on {first} and {second}");
        }
    }

    [MustDisposeResource]
    private static DreamValue BitXorAssignmentValues(DreamObjectTree objectTree, DreamList first, DreamValue second) {
        if (second.TryGetValueAsDreamList(out _))
            return BitXorValues(objectTree, new DreamValue(first), second);

        DreamList replacement = objectTree.CreateList();
        Dictionary<DreamValue, DreamValue> associations = first.GetAssociativeValues();
        var removed = false;

        // compound list XOR toggles one occurrence
        // BYOND removes the first match and leaves later duplicates in place
        foreach (DreamValue value in first.EnumerateValues()) {
            if (!removed && value == second) {
                removed = true;
                continue;
            }

            replacement.AddValue(value);
            if (associations.TryGetValue(value, out DreamValue associatedValue))
                replacement.SetValue(value, associatedValue);
        }

        if (!removed)
            replacement.AddValue(second);

        return new DreamValue(replacement);
    }

    private static void ReplaceListContents(DreamList target, DreamList replacement) {
        Dictionary<DreamValue, DreamValue> replacementAssociations = replacement.GetAssociativeValues();
        target.Cut();

        foreach (DreamValue value in replacement.EnumerateValues()) {
            target.AddValue(value);
            if (replacementAssociations.TryGetValue(value, out DreamValue associatedValue))
                target.SetValue(value, associatedValue);
        }
    }

    private static DreamValue ModulusValues(DreamValue first, DreamValue second) {
        if (first.TryGetValueAsInteger(out int firstInt) || first.IsNull)
            if (second.TryGetValueAsInteger(out int secondInt))
                return new DreamValue(firstInt % secondInt);

        throw new DMException($"Invalid modulus operation on {first} and {second}");
    }

    private static DreamValue ModulusModulusValues(DreamValue first, DreamValue second) {
        if (first.TryGetValueAsFloat(out float firstFloat) && second.TryGetValueAsFloat(out float secondFloat)) {
            // BYOND docs say that A %% B is equivalent to B * fract(A/B)
            // BREAKING CHANGE: The floating point precision is slightly different between OD and BYOND, giving slightly different values
            float fraction = firstFloat / secondFloat;
            fraction -= MathF.Truncate(fraction);
            return new DreamValue(fraction * secondFloat);
        }

        throw new DMException("Invalid modulusmodulus operation on " + first + " and " + second);
    }

    private static DreamValue CalculateGradient(List<DreamValue> gradientValues, DreamValue colorSpaceValue,
        DreamValue indexValue) {
        if (gradientValues.Count == 1)
            if (gradientValues[0].TryGetValueAsDreamList(out DreamList? gradientList))
                gradientValues = gradientList.GetValues();

        if (gradientValues.Count == 0)
            throw new DMException("bad gradient");

        if (!indexValue.TryGetValueAsFloat(out float index))
            throw new FormatException("Failed to parse index as float");

        colorSpaceValue.TryGetValueAsInteger(out int colorSpace);

        bool loop = gradientValues.Contains(new DreamValue("loop"));

        // true: look for int: false look for color
        var colorOrInt = true;

        float workingFloat = 0;
        float maxValue = 1;
        float minValue = 0;
        float leftBound = 0;
        float rightBound = 1;

        Color? left = null;
        Color? right = null;

        foreach (DreamValue value in gradientValues) {
            if (colorOrInt && value.TryGetValueAsFloat(out float flt)) { // Int
                colorOrInt = false;
                workingFloat = flt;
                maxValue = Math.Max(maxValue, flt);
                minValue = Math.Min(minValue, flt);
                continue; // Successful parse
            }

            if (!value.TryGetValueAsString(out string? strValue)) strValue = "#00000000";

            if (strValue == "loop") continue;

            if (!ColorHelpers.TryParseColor(strValue, out Color color))
                color = new Color(0, 0, 0, 0);

            if (loop && index >= maxValue) index %= maxValue;

            if (workingFloat >= index) {
                right = color;
                rightBound = workingFloat;
                break;
            }

            left = color;
            leftBound = workingFloat;

            if (colorOrInt) workingFloat = 1;

            colorOrInt = true;
        }

        // Convert the index to a 0-1 range
        float normalized = (index - leftBound) / (rightBound - leftBound);

        // Cheap way to make sure the gradient works at the extremes (eg 1 and 0)
        if (!left.HasValue || (right.HasValue && normalized.Equals(1f)) || (right.HasValue && normalized == 0)) {
            if (right?.AByte == 255) return new DreamValue(right.Value.ToHexNoAlpha().ToLower());

            return new DreamValue(right?.ToHex().ToLower() ?? "#00000000");
        }

        if (!right.HasValue) {
            if (left.Value.AByte == 255) return new DreamValue(left.Value.ToHexNoAlpha().ToLower());

            return new DreamValue(left.Value.ToHex().ToLower());
        }

        if (!left.HasValue && !right.HasValue) throw new InvalidOperationException("Failed to find any colors");

        Color returnVal;
        switch (colorSpace) {
            case 0: // RGB
                returnVal = Color.InterpolateBetween(left.GetValueOrDefault(), right.GetValueOrDefault(), normalized);
                break;
            case 1 or 2: // HSV/HSL
                Vector4 vec1 = Color.ToHsv(left.GetValueOrDefault());
                Vector4 vec2 = Color.ToHsv(right.GetValueOrDefault());

                // Some precision is lost when converting back to HSV at very small values this fixes that issue
                if (normalized < 0.05f) normalized += 0.001f;

                // This time it's overshooting
                // dw these numbers are insanely arbitrary
                if (normalized > 0.9f) normalized -= 0.00445f;

                float newHue;
                float delta = vec2.X - vec1.X;
                if (vec1.X > vec2.X) {
                    (vec1.X, vec2.X) = (vec2.X, vec1.X);
                    delta = -delta;
                    normalized = 1 - normalized;
                }

                if (delta > 0.5f) { // 180deg
                    vec1.X += 1f; // 360deg
                    newHue = (vec1.X + normalized * (vec2.X - vec1.X)) % 1; // 360deg
                } else {
                    newHue = vec1.X + normalized * delta;
                }

                Vector4 holder = new(
                    newHue,
                    vec1.Y + normalized * (vec2.Y - vec1.Y),
                    vec1.Z + normalized * (vec2.Z - vec1.Z),
                    vec1.W + normalized * (vec2.W - vec1.W));

                returnVal = Color.FromHsv(holder);
                break;
            default:
                throw new NotSupportedException("Cannot interpolate colorspace");
        }

        if (returnVal.AByte == 255)
            return new DreamValue(returnVal.ToHexNoAlpha().ToLower());
        return new DreamValue(returnVal.ToHex().ToLower());
    }

    private static DreamValue IconOperation(DMProcState state, BlendType blendType, DreamValue icon, DreamValue blend) {
        // Create a new /icon and blend it
        // Note that BYOND creates something other than an /icon, but it behaves the same as one in most reasonable interactions
        var iconObj = state.Proc.ObjectTree.CreateObject<DreamObjectIcon>(state.Proc.ObjectTree.Icon);
        if (!state.Proc.DreamResourceManager.TryLoadIcon(icon, out IconResource? from)) {
            iconObj.DecRef();
            throw new DMException($"Failed to create an icon from {from}");
        }

        iconObj.Icon.InsertStates(from, DreamValue.Null, DreamValue.Null, DreamValue.Null);
        DreamProcNativeIcon.Blend(iconObj.Icon, blend, blendType, 0, 0);
        return new DreamValue(iconObj);
    }

    #endregion Helpers

    #region Peephole Optimizations

    public static ProcStatus NullRef(DMProcState state) {
        state.AssignReference(state.ReadReference(), DreamValue.Null);
        return ProcStatus.Continue;
    }

    public static ProcStatus AssignNoPush(DMProcState state) {
        DreamReference reference = state.ReadReference();
        using DreamValue value = state.Pop();

        state.AssignReference(reference, value);
        return ProcStatus.Continue;
    }

    public static ProcStatus PushReferenceAndDereferenceField(DMProcState state) {
        DreamReference reference = state.ReadReference();
        string fieldName = state.ReadString();
        using DreamValue owner = state.GetReferenceValue(reference);
        using DreamValue value = state.DereferenceField(owner, fieldName);

        state.Push(value);
        return ProcStatus.Continue;
    }

    public static ProcStatus PushNStrings(DMProcState state) {
        int count = state.ReadInt();

        for (var i = 0; i < count; i++) {
            string str = state.ReadString();

            state.Push(new DreamValue(str));
        }

        return ProcStatus.Continue;
    }

    public static ProcStatus PushNFloats(DMProcState state) {
        int count = state.ReadInt();

        for (var i = 0; i < count; i++) {
            float flt = state.ReadFloat();

            state.Push(new DreamValue(flt));
        }

        return ProcStatus.Continue;
    }

    public static ProcStatus PushNRefs(DMProcState state) {
        int count = state.ReadInt();

        for (var i = 0; i < count; i++) {
            DreamReference reference = state.ReadReference();
            using DreamValue value = state.GetReferenceValue(reference);

            state.Push(value);
        }

        return ProcStatus.Continue;
    }

    public static ProcStatus PushNResources(DMProcState state) {
        int count = state.ReadInt();

        for (var i = 0; i < count; i++) {
            string resourcePath = state.ReadString();
            state.Push(new DreamValue(state.Proc.DreamResourceManager.LoadResource(resourcePath)));
        }

        return ProcStatus.Continue;
    }

    public static ProcStatus PushStringFloat(DMProcState state) {
        string str = state.ReadString();
        float flt = state.ReadFloat();

        state.Push(new DreamValue(str));
        state.Push(new DreamValue(flt));

        return ProcStatus.Continue;
    }

    public static ProcStatus JumpIfReferenceFalse(DMProcState state) {
        DreamReference reference = state.ReadReference();
        int jumpTo = state.ReadInt();
        using DreamValue value = state.GetReferenceValue(reference);

        if (!value.IsTruthy()) state.Jump(jumpTo);

        return ProcStatus.Continue;
    }

    public static ProcStatus SwitchOnFloat(DMProcState state) {
        float testValue = state.ReadFloat();
        int casePosition = state.ReadInt();
        using DreamValue test = state.Pop();
        if (test.TryGetValueAsFloat(out float value)) {
            if (testValue.Equals(value))
                state.Jump(casePosition);
            else
                state.Push(test);
        } else {
            state.Push(test);
        }

        return ProcStatus.Continue;
    }

    public static ProcStatus SwitchOnString(DMProcState state) {
        string testValue = state.ReadString();
        int casePosition = state.ReadInt();
        using DreamValue test = state.Pop();
        if (test.TryGetValueAsString(out string? value)) {
            if (testValue.Equals(value))
                state.Jump(casePosition);
            else
                state.Push(test);
        } else {
            state.Push(test);
        }

        return ProcStatus.Continue;
    }

    public static ProcStatus PushNOfStringFloat(DMProcState state) {
        int count = state.ReadInt();

        for (var i = 0; i < count; i++) {
            string str = state.ReadString();
            float flt = state.ReadFloat();

            state.Push(new DreamValue(str));
            state.Push(new DreamValue(flt));
        }

        return ProcStatus.Continue;
    }

    public static ProcStatus PushFloatAssign(DMProcState state) {
        float flt = state.ReadFloat();
        DreamReference reference = state.ReadReference();
        state.AssignReference(reference, new DreamValue(flt));
        return ProcStatus.Continue;
    }

    public static ProcStatus NPushFloatAssign(DMProcState state) {
        int count = state.ReadInt();

        for (var i = 0; i < count; i++) {
            float flt = state.ReadFloat();
            DreamReference reference = state.ReadReference();
            state.AssignReference(reference, new DreamValue(flt));
        }

        return ProcStatus.Continue;
    }

    public static ProcStatus CreateListNFloats(DMProcState state) {
        int size = state.ReadInt();
        DreamList list = state.Proc.ObjectTree.CreateList(size);

        for (var i = 0; i < size; i++) {
            float flt = state.ReadFloat();

            list.AddValue(new DreamValue(flt));
        }

        state.Push(new DreamValue(list));
        list.DecRef();
        return ProcStatus.Continue;
    }

    public static ProcStatus CreateListNStrings(DMProcState state) {
        int size = state.ReadInt();
        DreamList list = state.Proc.ObjectTree.CreateList(size);

        for (var i = 0; i < size; i++) {
            string str = state.ReadString();

            list.AddValue(new DreamValue(str));
        }

        state.Push(new DreamValue(list));
        list.DecRef();
        return ProcStatus.Continue;
    }

    public static ProcStatus CreateListNRefs(DMProcState state) {
        int size = state.ReadInt();
        DreamList list = state.Proc.ObjectTree.CreateList(size);

        for (var i = 0; i < size; i++) {
            DreamReference reference = state.ReadReference();
            using DreamValue value = state.GetReferenceValue(reference);

            list.AddValue(value);
        }

        state.Push(new DreamValue(list));
        list.DecRef();
        return ProcStatus.Continue;
    }

    public static ProcStatus CreateListNResources(DMProcState state) {
        int size = state.ReadInt();
        DreamList list = state.Proc.ObjectTree.CreateList(size);

        for (var i = 0; i < size; i++) {
            string resourcePath = state.ReadString();
            list.AddValue(new DreamValue(state.Proc.DreamResourceManager.LoadResource(resourcePath)));
        }

        state.Push(new DreamValue(list));
        list.DecRef();
        return ProcStatus.Continue;
    }

    public static ProcStatus IsTypeDirect(DMProcState state) {
        using DreamValue value = state.Pop();
        int typeId = state.ReadInt();
        TreeEntry typeValue = state.Proc.ObjectTree.Types[typeId];

        if (value.TryGetValueAsDreamObject(out DreamObject? dreamObject) && dreamObject != null)
            state.Push(new DreamValue(dreamObject.IsSubtypeOf(typeValue) ? 1 : 0));
        else
            state.Push(new DreamValue(0));

        return ProcStatus.Continue;
    }

    public static ProcStatus ReturnFloat(DMProcState state) {
        state.SetReturn(new DreamValue(state.ReadFloat()));
        return ProcStatus.Returned;
    }

    #endregion
}
