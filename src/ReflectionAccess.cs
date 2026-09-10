using System;
using System.Reflection;

namespace WithUMM
{
    internal static class ReflectionAccess
    {
        internal const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
        internal static MethodInfo Method(Type type, string name, params Type[] args) =>
            type?.GetMethod(name, All, null, args, null) ?? throw new MissingMethodException(type?.FullName, name);
        internal static object Get(object instance, string name)
        {
            var type = instance as Type ?? instance.GetType();
            var target = instance is Type ? null : instance;
            var field = type.GetField(name, All);
            return field != null ? field.GetValue(target) : type.GetProperty(name, All)?.GetValue(target, null);
        }
        internal static void Set(object instance, string name, object value)
        {
            var type = instance as Type ?? instance.GetType();
            var target = instance is Type ? null : instance;
            var field = type.GetField(name, All);
            if (field != null) field.SetValue(target, value);
            else (type.GetProperty(name, All) ?? throw new MissingMemberException(type.FullName, name)).SetValue(target, value, null);
        }
        internal static Exception Unwrap(Exception e) => e is TargetInvocationException t && t.InnerException != null ? t.InnerException : e;
    }
}
