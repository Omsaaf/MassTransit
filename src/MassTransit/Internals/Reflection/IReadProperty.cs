﻿namespace MassTransit.Internals.Reflection
{
    public interface IReadProperty<in T, out TProperty> :
        IReadProperty<T>
        where T : class
    {
        TProperty Get(T entity);
    }


    public interface IReadProperty<in T>
    {
    }
}
