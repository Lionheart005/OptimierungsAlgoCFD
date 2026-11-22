using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace FluidForge.Core
{
    /// <summary>
    /// Reflection-based JSON Schema (Draft 2020-12) generator for FluidForge configuration classes.
    /// Lives strictly in <c>Core/</c> with zero dependencies on outer layers.
    /// </summary>
    public static class JsonSchemaGenerator
    {
        public const string SchemaDraft202012 = "https://json-schema.org/draft/2020-12/schema";

        private static readonly JsonSerializerOptions PrettyOptions = new()
        {
            WriteIndented = true
        };

        /// <summary>
        /// Generates a Draft 2020-12 JSON Schema for <paramref name="rootType"/>.
        /// </summary>
        public static JsonObject Generate(Type rootType, string? title = null, string? description = null)
        {
            if (rootType == null) throw new ArgumentNullException(nameof(rootType));

            var schema = new JsonObject
            {
                ["$schema"] = SchemaDraft202012,
                ["title"] = string.IsNullOrWhiteSpace(title) ? rootType.Name : title,
            };

            if (!string.IsNullOrWhiteSpace(description))
                schema["description"] = description;

            JsonObject body = BuildObjectSchema(rootType, includeRootSchemaProperty: true);
            foreach (KeyValuePair<string, JsonNode?> kvp in body)
                schema[kvp.Key] = kvp.Value?.DeepClone();

            return schema;
        }

        /// <summary>
        /// Serializes the generated JSON Schema for <paramref name="rootType"/> as formatted JSON.
        /// </summary>
        public static string GenerateJson(Type rootType, string? title = null, string? description = null)
            => Generate(rootType, title, description).ToJsonString(PrettyOptions) + Environment.NewLine;

        private static JsonObject BuildObjectSchema(Type type, bool includeRootSchemaProperty)
        {
            var properties = new JsonObject();
            if (includeRootSchemaProperty)
            {
                properties["$schema"] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] = "Optional JSON Schema reference for editor validation and autocomplete."
                };
            }

            object? defaultInstance = TryCreateDefaultInstance(type);

            foreach (PropertyInfo prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                         .Where(p => p.CanWrite && p.GetIndexParameters().Length == 0)
                         .OrderBy(p => p.Name, StringComparer.Ordinal))
            {
                JsonObject propSchema = BuildTypeSchema(prop.PropertyType, declaringType: type, propertyName: prop.Name);

                if (defaultInstance != null)
                {
                    JsonNode? defaultNode = TrySerializeScalarDefault(prop.GetValue(defaultInstance), prop.PropertyType);
                    if (defaultNode != null)
                        propSchema["default"] = defaultNode;
                }

                properties[prop.Name] = propSchema;
            }

            return new JsonObject
            {
                ["type"] = "object",
                ["properties"] = properties,
                ["additionalProperties"] = false
            };
        }

        private static JsonObject BuildTypeSchema(Type type, Type? declaringType, string? propertyName)
        {
            Type effective = Nullable.GetUnderlyingType(type) ?? type;

            if (effective == typeof(string))
            {
                var strSchema = new JsonObject { ["type"] = "string" };
                if (declaringType == typeof(SimulationConfig)
                    && string.Equals(propertyName, nameof(SimulationConfig.Preset), StringComparison.Ordinal))
                {
                    var enumValues = new JsonArray
                    {
                        JsonValue.Create(string.Empty),
                        JsonValue.Create("Custom"),
                        JsonValue.Create("External2D"),
                        JsonValue.Create("External3D"),
                        JsonValue.Create("Internal3D")
                    };
                    foreach (PresetDefinition preset in SimulationPresets.All)
                        enumValues.Add(JsonValue.Create(preset.Name));

                    strSchema["enum"] = enumValues;
                    strSchema["description"] = "Physics & workflow preset (Layer 1.5), e.g. 'External3D:StandardRans', 'External2D:StandardRans', or 'Custom'/empty for none.";
                }
                return strSchema;
            }

            if (effective == typeof(bool))
                return new JsonObject { ["type"] = "boolean" };

            if (effective == typeof(int) || effective == typeof(long) || effective == typeof(short) || effective == typeof(byte))
                return new JsonObject { ["type"] = "integer" };

            if (effective == typeof(float) || effective == typeof(double) || effective == typeof(decimal))
                return new JsonObject { ["type"] = "number" };

            if (effective.IsEnum)
            {
                var enumArr = new JsonArray();
                foreach (string name in Enum.GetNames(effective))
                    enumArr.Add(JsonValue.Create(name));
                return new JsonObject
                {
                    ["type"] = "string",
                    ["enum"] = enumArr
                };
            }

            if (TryGetDictionaryValueType(effective, out Type? dictValueType))
            {
                return new JsonObject
                {
                    ["type"] = "object",
                    ["additionalProperties"] = BuildTypeSchema(dictValueType!, declaringType: null, propertyName: null)
                };
            }

            if (TryGetSequenceElementType(effective, out Type? elementType))
            {
                return new JsonObject
                {
                    ["type"] = "array",
                    ["items"] = BuildTypeSchema(elementType!, declaringType: null, propertyName: null)
                };
            }

            if (HasOwnProperties(effective))
                return BuildObjectSchema(effective, includeRootSchemaProperty: false);

            return new JsonObject();
        }

        private static bool HasOwnProperties(Type type)
        {
            if (type == typeof(string) || type.IsPrimitive || type.IsEnum) return false;
            if (typeof(IEnumerable).IsAssignableFrom(type)) return false;
            return type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Any(p => p.CanWrite);
        }

        private static bool TryGetDictionaryValueType(Type type, out Type? valueType)
        {
            valueType = null;
            foreach (Type iface in GetSelfAndInterfaces(type))
            {
                if (!iface.IsGenericType) continue;
                Type def = iface.GetGenericTypeDefinition();
                if (def == typeof(IDictionary<,>) || def == typeof(IReadOnlyDictionary<,>))
                {
                    valueType = iface.GetGenericArguments()[1];
                    return true;
                }
            }
            return false;
        }

        private static bool TryGetSequenceElementType(Type type, out Type? elementType)
        {
            elementType = null;
            if (type == typeof(string)) return false;
            if (type.IsArray)
            {
                elementType = type.GetElementType();
                return elementType != null;
            }

            foreach (Type iface in GetSelfAndInterfaces(type))
            {
                if (iface.IsGenericType && iface.GetGenericTypeDefinition() == typeof(IEnumerable<>))
                {
                    elementType = iface.GetGenericArguments()[0];
                    return true;
                }
            }
            return false;
        }

        private static IEnumerable<Type> GetSelfAndInterfaces(Type type)
            => new[] { type }.Concat(type.GetInterfaces());

        private static object? TryCreateDefaultInstance(Type type)
        {
            try
            {
                return Activator.CreateInstance(type);
            }
            catch
            {
                return null;
            }
        }

        private static JsonNode? TrySerializeScalarDefault(object? value, Type propertyType)
        {
            if (value == null) return null;

            Type effective = Nullable.GetUnderlyingType(propertyType) ?? propertyType;
            if (effective == typeof(string)) return JsonValue.Create((string)value);
            if (effective == typeof(bool)) return JsonValue.Create((bool)value);
            if (effective == typeof(int)) return JsonValue.Create((int)value);
            if (effective == typeof(long)) return JsonValue.Create((long)value);
            if (effective == typeof(float))
            {
                float f = (float)value;
                return float.IsFinite(f) ? JsonValue.Create(f) : null;
            }
            if (effective == typeof(double))
            {
                double d = (double)value;
                return double.IsFinite(d) ? JsonValue.Create(d) : null;
            }
            if (effective == typeof(decimal)) return JsonValue.Create((decimal)value);
            if (effective.IsEnum) return JsonValue.Create(value.ToString());

            return null;
        }
    }
}
